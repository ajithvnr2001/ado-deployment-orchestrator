import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import type { StatusUpdate } from './api'

const HUB_URL = `${import.meta.env.VITE_API_BASE ?? ''}/hubs/deployment`

export function createStatusConnection(onUpdate: (update: StatusUpdate) => void): HubConnection {
  const connection = new HubConnectionBuilder()
    .withUrl(HUB_URL)
    .configureLogging(LogLevel.Information)
    .withAutomaticReconnect()
    .build()

  connection.on('statusUpdated', (update: StatusUpdate) => onUpdate(update))
  return connection
}

/** Subscribes to a deployment request's live status group once the connection is established. */
export async function subscribe(connection: HubConnection, requestId: string): Promise<void> {
  if (connection.state === 'Disconnected') await connection.start()
  await connection.invoke('Subscribe', requestId)
}
