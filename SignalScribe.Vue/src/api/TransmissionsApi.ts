import http from "@/api/axios";

export interface SegmentDto {
  id: number;
  startMs: number;
  endMs: number;
  transcript: string | null;
  callsign: string | null;
  speakerId: number | null;
}

export interface TransmissionDto {
  channelId: number;
  id: number;
  frequencyHz: number;
  channelLabel: string;
  startUtc: string;
  endUtc: string | null;
  isDouble: boolean;
  audioPath: string;
  status: string;
  segments: SegmentDto[];
}

export class TransmissionsApi {
  static async getRecent(limit = 50, channelId?: number, before?: string): Promise<TransmissionDto[]> {
    const response = await http.get<TransmissionDto[]>("/api/v0/transmissions", {
      params: { limit, channelId, before },
    });
    return response.data;
  }

  static audioUrl(transmissionId: number): string {
    return `/api/v0/audio/${transmissionId}`;
  }

  static async reprocess(transmissionId: number): Promise<void> {
    await http.post(`/api/v0/transmissions/${transmissionId}/reprocess`);
  }

  /** Re-transcribe in bulk — a channel or everything; onlyMissing skips clips that already have text. */
  static async reprocessBulk(channelId?: number, onlyMissing = false): Promise<number> {
    return (await http.post<number>("/api/v0/transmissions/reprocess", null, { params: { channelId, onlyMissing } })).data;
  }
}
