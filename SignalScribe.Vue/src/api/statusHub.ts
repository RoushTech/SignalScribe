import { HubConnectionBuilder, type HubConnection } from "@microsoft/signalr";

export interface SdrDeviceInfo {
  serial: string;
  model: string;
  inUse: boolean;
}

export interface ActiveGate {
  frequencyHz: number;
  seconds: number;
  peakDbfs: number;
  known: boolean;
}

export interface ServiceStatusUpdate {
  service: string;
  state: string;
  timestampUtc: string;
  details: Record<string, string>;
  devices: SdrDeviceInfo[] | null;
  gates: ActiveGate[] | null;
}

export interface SpectrumRow {
  centerFrequencyHz: number;
  spanHz: number;
  timestampUtc: string;
  minDb: number;
  maxDb: number;
  bins: string; // byte[] arrives base64-encoded over SignalR JSON
}

/** Connects to /hubs/status, seeds with the snapshot, then invokes callbacks per live push. */
export async function connectStatusHub(
  onUpdate: (update: ServiceStatusUpdate) => void,
  onSpectrum?: (row: SpectrumRow) => void,
  onTransmission?: (t: unknown) => void,
): Promise<HubConnection> {
  const connection = new HubConnectionBuilder().withUrl("/hubs/status").withAutomaticReconnect().build();
  connection.on("statusChanged", onUpdate);
  if (onSpectrum) {
    connection.on("spectrum", onSpectrum);
  }
  if (onTransmission) {
    // Fires when a clip is ingested and again when its transcript lands.
    connection.on("transmissionChanged", onTransmission);
  }
  await connection.start();
  const snapshot = await connection.invoke<ServiceStatusUpdate[]>("GetSnapshot");
  snapshot.forEach(onUpdate);
  return connection;
}

export function decodeBins(row: SpectrumRow): Uint8Array {
  return Uint8Array.from(atob(row.bins), (c) => c.charCodeAt(0));
}
