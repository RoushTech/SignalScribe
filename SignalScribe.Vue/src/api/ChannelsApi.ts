import http from "@/api/axios";

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
  notes: string | null;
  noiseFloorDbfs: number | null;
  measuredCtcssToneHz: number | null;
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
  ctcssToneHz: number | null;
  notes: string | null;
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
