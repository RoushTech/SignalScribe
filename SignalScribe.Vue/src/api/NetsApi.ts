import http from "@/api/axios";

export enum NetScheduleSource {
  Manual = 0,
  Mined = 1,
}

export interface NetDto {
  id: number;
  channelId: number;
  name: string | null;
  description: string | null;
  source: NetScheduleSource;
  dayOfWeekUtc: number | null;
  startTimeUtc: string | null;
  durationMinutes: number | null;
  sessionCount: number;
  lastSessionUtc: string | null;
}

export interface NetUpsertRequest {
  channelId: number;
  name: string | null;
  description: string | null;
  dayOfWeekUtc: number | null;
  startTimeUtc: string | null;
  durationMinutes: number | null;
}

export class NetsApi {
  static async getForChannel(channelId: number): Promise<NetDto[]> {
    return (await http.get<NetDto[]>("/api/v0/nets", { params: { channelId } })).data;
  }

  static async create(request: NetUpsertRequest): Promise<NetDto> {
    return (await http.post<NetDto>("/api/v0/nets", request)).data;
  }

  static async update(id: number, request: NetUpsertRequest): Promise<NetDto> {
    return (await http.put<NetDto>(`/api/v0/nets/${id}`, request)).data;
  }

  static async remove(id: number): Promise<void> {
    await http.delete(`/api/v0/nets/${id}`);
  }
}
