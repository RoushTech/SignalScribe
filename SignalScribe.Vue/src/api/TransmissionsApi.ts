import http from "@/api/axios";
import type { DetectedMode } from "@/lib/detectedMode";

/** One field out of a decoded digital-mode header, in the order the mode presents it. */
export interface HeaderFieldDto {
  name: string;
  value: string;
}

export interface SegmentDto {
  id: number;
  startMs: number;
  endMs: number;
  transcript: string | null;
  callsign: string | null;
  speakerId: number | null;
  /** Human reading of a decoded data frame; null for speech. The raw frame stays in `transcript`. */
  summary: string | null;
  /**
   * Every field decoded out of a digital-mode header (D-STAR today, Fusion and others later); null
   * on ordinary speech segments. `transcript` carries the one-line summary of the same header.
   */
  headerFields?: HeaderFieldDto[] | null;
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
  ctcssHz: number | null;
  dcsCode: number | null;
  channelCtcssHz: number | null;
  channelDcsCode: number | null;
  mode: DetectedMode;
  channelMode: DetectedMode | null;
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
