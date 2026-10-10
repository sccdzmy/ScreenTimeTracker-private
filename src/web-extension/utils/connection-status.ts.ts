export type ConnectionStatus = 'connected' | 'disconnected' | 'unpaired' | 'expired' | 'http-error'

export const defaultConnectionStatus: ConnectionStatus = 'unpaired'

export const connectionStatusStorage = storage.defineItem<ConnectionStatus>('local:connectionStatus', {
    defaultValue: defaultConnectionStatus,
});