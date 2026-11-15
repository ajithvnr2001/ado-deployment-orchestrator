import { useCallback, useEffect, useRef, useState } from 'react'
import './App.css'
import {
  approve, getAudit, getRequest, startDeployment, submitInputs,
  type AuditEntry, type DeploymentRequest, type RequestLifecycleStatus, type StatusUpdate,
} from './api'
import { createStatusConnection } from './signalr'
import type { HubConnection } from '@microsoft/signalr'

interface ChatMessage {
  id: number
  role: 'user' | 'system'
  text: string
}

const STATUS_LABELS: Record<RequestLifecycleStatus, string> = {
  Draft: 'Missing inputs',
  Validation: 'Validating',
  SecurityApproval: 'Awaiting security approval',
  ReviewTeamApproval: 'Awaiting review team approval',
  PipelineRunning: 'Pipeline running',
  CompletedSuccess: 'Completed (Success)',
  CompletedFailed: 'Completed (Failed)',
  Decommissioned: 'Decommissioned',
}

function App() {
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [input, setInput] = useState('')
  const [subscription, setSubscription] = useState('sub-payments')
  const [state, setState] = useState('dev')
  const [current, setCurrent] = useState<DeploymentRequest | null>(null)
  const [audit, setAudit] = useState<AuditEntry[]>([])
  const [connection, setConnection] = useState<HubConnection | null>(null)
  const [busy, setBusy] = useState(false)
  const nextId = useRef(1)
  const feedRef = useRef<HTMLDivElement>(null)
  const connRef = useRef<HubConnection | null>(null)
  const lastStatusKey = useRef<string | null>(null)

  const say = useCallback((role: 'user' | 'system', text: string) => {
    setMessages((prev) => [...prev, { id: nextId.current++, role, text }])
  }, [])

  const applyUpdate = useCallback((update: StatusUpdate) => {
    // Dedupe: polling and the SignalR push channel both feed this handler.
    const key = `${update.requestId}:${update.status}:${update.message ?? ''}`
    if (lastStatusKey.current === key) return
    lastStatusKey.current = key
    say('system', `[live] ${update.message ?? update.status}`)
    setCurrent((prev) => (prev && prev.id === update.requestId
      ? { ...prev, status: update.status, statusMessage: update.message }
      : prev))
  }, [say])

  const ensureHub = useCallback((requestId?: string) => {
    // (Re)connect the hub with ?requestId= so the server adds us to the group on connect.
    // Fire-and-forget: SignalR issues must never block the chat flow —
    // the status-polling fallback keeps the UI correct regardless.
    const old = connRef.current
    if (old) { void old.stop().catch(() => undefined) }
    const conn = createStatusConnection(applyUpdate, requestId)
    connRef.current = conn
    setConnection(conn)
    void conn.start().catch(() => undefined)
  }, [applyUpdate])

  // Warm up the hub connection on page load (no group yet — group joins per request)
  useEffect(() => {
    ensureHub(undefined)
    return () => { void connRef.current?.stop() }
  }, [ensureHub])

  const refreshAudit = useCallback(async (id: string) => {
    try { setAudit(await getAudit(id)) } catch { /* audit is best-effort in the UI */ }
  }, [])

  // Status-polling fallback: guarantees progress display even if the push
  // channel (SignalR) is unavailable (WebSocket-restricted networks, proxies).
  useEffect(() => {
    const active = current && !['Draft', 'CompletedSuccess', 'CompletedFailed', 'Decommissioned'].includes(current.status)
    if (!current || !active) return
    const id = current.id
    const timer = setInterval(async () => {
      try {
        const fresh = await getRequest(id)
        applyUpdate({ requestId: fresh.id, status: fresh.status, message: fresh.statusMessage, timestampUtc: '' })
        if (['PipelineRunning', 'CompletedSuccess', 'CompletedFailed'].includes(fresh.status)) void refreshAudit(id)
      } catch { /* transient errors ignored — next tick retries */ }
    }, 2000)
    return () => clearInterval(timer)
  }, [current, applyUpdate, refreshAudit])

  useEffect(() => {
    feedRef.current?.scrollTo({ top: feedRef.current.scrollHeight })
  }, [messages])

  const handleSend = useCallback(async () => {
    const text = input.trim()
    if (!text || busy) return
    setBusy(true)
    setInput('')
    say('user', text)

    try {
      let request = current
      if (!request) {
        // Phase 1 — deployment initiation. The orchestrator infers the resource type.
        request = await startDeployment({
          requestedBy: 'chat-user@contoso.com',
          subscription,
          state,
          resourceName: text,
        })
        setCurrent(request)
        ensureHub(request.id)
        await refreshAudit(request.id)
        say('system', `Request ${request.id.slice(0, 8)} — status: ${STATUS_LABELS[request.status]}`)
        if (request.resourceType) say('system', `Inferred resource type: ${request.resourceType} (pipeline ${request.pipelineId}, folder ${request.terraformScriptFolder})`)
      }

      if (request.status === 'Draft' && request.missingFields.length > 0) {
        // Phase 3 — missing input handling: treat each "key=value" token as a field.
        const provided: Record<string, string> = {}
        for (const token of text.split(/[\s,]+/)) {
          const eq = token.indexOf('=')
          if (eq > 0) provided[token.slice(0, eq)] = token.slice(eq + 1)
        }
        if (Object.keys(provided).length > 0) {
          const updated = await submitInputs(request.id, provided)
          setCurrent(updated)
          await refreshAudit(request.id)
          say('system', `Inputs accepted — status: ${STATUS_LABELS[updated.status]}`)
          if (updated.missingFields.length > 0) {
            say('system', `Still missing: ${updated.missingFields.join(', ')}  (send as key=value)`)
          }
        } else {
          say('system', `Please provide: ${request.missingFields.map((f) => `${f}=...`).join(', ')}`)
        }
      } else if (request.status === 'SecurityApproval' || request.status === 'ReviewTeamApproval') {
        // Phase 2 — approval gates
        const updated = await approve(request.id, 'chat-approver@contoso.com', text)
        setCurrent(updated)
        await refreshAudit(request.id)
        say('system', `Approved — status: ${STATUS_LABELS[updated.status]}`)
        if (updated.pipelineRunId) say('system', `Pipeline run ${updated.pipelineRunId} triggered — watching live status`)
      } else if (request.status === 'PipelineRunning') {
        say('system', 'Deployment already running — live updates will arrive automatically')
      } else if (request.status === 'CompletedSuccess' || request.status === 'CompletedFailed') {
        // Start a fresh request
        setCurrent(null)
        setAudit([])
        say('system', 'Previous request finished — sending a new deployment request')
        request = await startDeployment({
          requestedBy: 'chat-user@contoso.com', subscription, state, resourceName: text,
        })
        setCurrent(request)
        ensureHub(request.id)
        await refreshAudit(request.id)
        say('system', `Request ${request.id.slice(0, 8)} — status: ${STATUS_LABELS[request.status]}`)
      }
    } catch (err) {
      say('system', `Error: ${err instanceof Error ? err.message : String(err)}`)
    } finally {
      setBusy(false)
    }
  }, [input, busy, current, connection, subscription, state, say, refreshAudit])

  const handleShowAudit = useCallback(async () => {
    if (!current) { say('system', 'No active request') ; return }
    await refreshAudit(current.id)
    say('system', `Audit trail (${audit.length} entries):\n${audit.map((a) => `${a.timestampUtc.slice(11, 19)} ${a.action} — ${a.detail}`).join('\n')}`)
  }, [current, audit, say, refreshAudit])

  const statusColor: Record<RequestLifecycleStatus, string> = {
    Draft: '#f59e0b', Validation: '#3b82f6', SecurityApproval: '#8b5cf6',
    ReviewTeamApproval: '#8b5cf6', PipelineRunning: '#3b82f6',
    CompletedSuccess: '#22c55e', CompletedFailed: '#ef4444', Decommissioned: '#6b7280',
  }

  return (
    <div className="app">
      <header className="header">
        <h1>Azure DevOps Deployment Orchestration</h1>
        <p>Chat Deployment Controller — ADO Agent | Terraform | GitHub | Monitoring | End-to-End Deployment Flow</p>
      </header>

      <div className="env-row">
        <label>Subscription <input value={subscription} onChange={(e) => setSubscription(e.target.value)} /></label>
        <label>State / env <input value={state} onChange={(e) => setState(e.target.value)} /></label>
        {current && (
          <span className="status-pill" style={{ background: statusColor[current.status] }}>
            {STATUS_LABELS[current.status]}
          </span>
        )}
        <button className="audit-btn" onClick={handleShowAudit}>Show audit trail</button>
      </div>

      <div className="chat" ref={feedRef}>
        {messages.length === 0 && (
          <div className="empty-hint">
            Try: <em>"create a new web app for the payments team"</em><br />
            The agent infers the resource type, validates, asks for missing fields
            (send <code>key=value</code>), runs approvals and streams live pipeline status.
          </div>
        )}
        {messages.map((m) => (
          <div key={m.id} className={`message ${m.role}${m.text.startsWith('[live]') ? ' live' : ''}`}>
            <pre>{m.text}</pre>
          </div>
        ))}
        {current && current.status === 'PipelineRunning' && <div className="spinner">Watching pipeline run {current.pipelineRunId}…</div>}
      </div>

      <div className="input-row">
        <input
          value={input}
          disabled={busy}
          placeholder={current?.status === 'Draft' && current.missingFields.length > 0
            ? `Provide: ${current.missingFields.map((f) => `${f}=...`).join(', ')}`
            : current?.status.startsWith('Completed') ? 'Describe your next deployment…' : 'Describe the deployment or answer the agent…'}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') void handleSend() }}
        />
        <button onClick={handleSend} disabled={busy}>Send</button>
      </div>
    </div>
  )
}

export default App
