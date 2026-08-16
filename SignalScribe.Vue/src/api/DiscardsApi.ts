import { DiscardReason } from "@/lib/discardReason";
import http from "@/api/axios";

export interface DiscardDto {
  id: number;
  frequencyHz: number;
  startUtc: string;
  durationMs: number;
  reason: DiscardReason;
  peakDbfs: number;
  voicedMs: number;
  speechBandRatio: number;
  modulationDepth: number;
  syllableRateHz: number;
  sustainedTone: boolean;
  ctcssHz: number | null;
  dcsCode: number | null;
}

export interface ReasonCountDto {
  reason: DiscardReason;
  count: number;
}

export interface DiscardStatsDto {
  total: number;
  oldestUtc: string | null;
  byReason: ReasonCountDto[];
}

export class DiscardsApi {
  static async getRecent(limit = 100): Promise<DiscardDto[]> {
    return (await http.get<DiscardDto[]>("/api/v0/discards", { params: { limit } })).data;
  }

  static async getStats(): Promise<DiscardStatsDto> {
    return (await http.get<DiscardStatsDto>("/api/v0/discards/stats")).data;
  }

  static audioUrl(id: number): string {
    return `/api/v0/discards/${id}/audio`;
  }

  static async purgeAll(): Promise<number> {
    return (await http.delete<number>("/api/v0/discards")).data;
  }
}
