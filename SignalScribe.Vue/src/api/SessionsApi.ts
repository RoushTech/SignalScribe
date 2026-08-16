import http from "@/api/axios";

export interface SessionDto {
  id: number;
  channelId: number;
  channelLabel: string;
  startUtc: string;
  endUtc: string | null;
  isNet: boolean;
  netId: number | null;
  netName: string | null;
  transmissionCount: number;
  summary: string | null;
  summaryModel: string | null;
}

export class SessionsApi {
  static async getForChannel(channelId: number, limit = 50): Promise<SessionDto[]> {
    return (await http.get<SessionDto[]>("/api/v0/sessions", { params: { channelId, limit } })).data;
  }
}
