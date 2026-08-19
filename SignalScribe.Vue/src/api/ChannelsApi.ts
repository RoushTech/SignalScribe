import http from "@/api/axios";
import type { DetectedMode } from "@/lib/detectedMode";

export enum ChannelType {
  Unknown = 0,
  Simplex = 1,
  RepeaterOutput = 2,
}

export interface ChannelDto {
  id: number;
  frequencyHz: number;
  label: string;
  type: ChannelType;
  enabled: boolean;
  callsign: string | null;
  description: string | null;
  ctcssToneHz: number | null;
  dcsCode: number | null;
  notes: string | null;
  noiseFloorDbfs: number | null;
  /** True while capture keeps re-learning the floor; false means the stored floor is pinned. */
  adaptiveSquelch: boolean;
  measuredCtcssToneHz: number | null;
  measuredDcsCode: number | null;
  modulation: DetectedMode | null;
  measuredMode: DetectedMode | null;
  modeUpdatedUtc: string | null;
  transmissionCount: number;
  lastHeardUtc: string | null;
  autoDisabledReason: string | null;
  lastSpeechUtc: string | null;
}

export interface ChannelUpsertRequest {
  frequencyHz: number;
  label: string;
  type: ChannelType;
  enabled: boolean;
  callsign: string | null;
  description: string | null;
  // CTCSS and DCS are alternative systems, never both — the server clears one when the other is set.
  ctcssToneHz: number | null;
  dcsCode: number | null;
  notes: string | null;
  modulation: DetectedMode | null;
  adaptiveSquelch: boolean;
  /** Only honoured by the server when adaptiveSquelch is false. */
  noiseFloorDbfs: number | null;
}

export class ChannelsApi {
  static async getAll(): Promise<ChannelDto[]> {
    return (await http.get<ChannelDto[]>("/api/v0/channels")).data;
  }

  static async get(id: number): Promise<ChannelDto> {
    return (await http.get<ChannelDto>(`/api/v0/channels/${id}`)).data;
  }

  static async create(request: ChannelUpsertRequest): Promise<ChannelDto> {
    return (await http.post<ChannelDto>("/api/v0/channels", request)).data;
  }

  static async update(id: number, request: ChannelUpsertRequest): Promise<ChannelDto> {
    return (await http.put<ChannelDto>(`/api/v0/channels/${id}`, request)).data;
  }
}
