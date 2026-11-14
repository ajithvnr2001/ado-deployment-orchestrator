export type RequestLifecycleStatus =
  | 'Draft' | 'Validation' | 'SecurityApproval' | 'ReviewTeamApproval'
  | 'PipelineRunning' | 'CompletedSuccess' | 'CompletedFailed' | 'Decommissioned'

export interface DeploymentRequest {
  id: string
  workItemId: number
  requestedBy: string
  subscription: string | null
  state: string | null
  resourceType: string | null
  resourceName: string | null
  fields: Record<string, string>
  missingFields: string[]
  status: RequestLifecycleStatus
  validationError: string | null
  securityApproved: boolean
  securityApprovedBy: string | null
  reviewApproved: boolean
  reviewApprovedBy: string | null
  pipelineRunId: number | null
  pipelineId: string | null
  terraformScriptFolder: string | null
  statusMessage: string | null
  createdUtc: string
  updatedUtc: string
}

export interface StatusUpdate {
  requestId: string
  status: RequestLifecycleStatus
  message: string | null
  timestampUtc: string
}

export interface AuditEntry {
  requestId: string
  action: string
  detail: string
  actor: string
  timestampUtc: string
}

const API_BASE = import.meta.env.VITE_API_BASE ?? ''

async function handle<T>(responsePromise: Promise<Response>): Promise<T> {
  const response = await responsePromise
  if (!response.ok) {
    const body = await response.text()
    throw new Error(`API ${response.status}: ${body}`)
  }
  return response.json() as Promise<T>
}

export async function startDeployment(input: {
  requestedBy: string
  subscription: string
  state: string
  resourceType?: string
  resourceName?: string
  fields?: Record<string, string>
}): Promise<DeploymentRequest> {
  return handle(fetch(`${API_BASE}/api/deployments`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  }))
}

export async function submitInputs(id: string, fields: Record<string, string>): Promise<DeploymentRequest> {
  return handle(fetch(`${API_BASE}/api/deployments/${id}/inputs`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ fields }),
  }))
}

export async function approve(id: string, approvedBy: string, comment?: string): Promise<DeploymentRequest> {
  return handle(fetch(`${API_BASE}/api/deployments/${id}/approve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ approvedBy, comment }),
  }))
}

export async function getRequest(id: string): Promise<DeploymentRequest> {
  return handle(fetch(`${API_BASE}/api/deployments/${id}`))
}

export async function getAudit(id: string): Promise<AuditEntry[]> {
  return handle(fetch(`${API_BASE}/api/deployments/${id}/audit`))
}
