import { HubConnection, HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr'
import type { StatusUpdate } from './api'

const HUB_BASE = `${import.meta.env.VITE_API_BASE ?? ''}/hubs/deployment`

export function createStatusConnection(onUpdate: (update: StatusUpdate) => void, requestId?: string): HubConnection {
  // Transport is configurable: headless scraping engines (obscura) and some
  // corporate proxies cannot do WebSockets — SSE works everywhere.
  // Default (unset) = automatic negotiation (WebSockets preferred).
  const transport = import.meta.env.VITE_SIGNALR_TRANSPORT as keyof typeof HttpTransportType | undefined
  let builder = new HubConnectionBuilder()
  builder = transport
    ? builder.withUrl(requestId ? `${HUB_BASE}?requestId=${requestId}` : HUB_BASE, { transport: HttpTransportType[transport] })
    : builder.withUrl(requestId ? `${HUB_BASE}?requestId=${requestId}` : HUB_BASE)

  const connection = builder
    .configureLogging(LogLevel.Information)
    .withAutomaticReconnect()
    .build()

  connection.on('statusUpdated', (update: StatusUpdate) => onUpdate(update))
  return connection
}

/** Connects and starts. Group membership is established via ?requestId= on connect. */
export async function connectToRequest(
  connection: HubConnection,
  _requestId: string | undefined,
): Promise<void> {
  if (connection.state !== 'Disconnected') await connection.stop()
  await connection.start()
}
