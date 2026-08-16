import http from "@/api/axios";

export interface TypeCountDto {
  type: number;
  count: number;
}

export interface ProcessingStatsDto {
  pending: number;
  leased: number;
  completed: number;
  failed: number;
  oldestPendingUtc: string | null;
  pendingByType: TypeCountDto[];
}

export interface FailedJobDto {
  id: number;
  type: number;
  attempts: number;
  error: string | null;
  createdUtc: string;
  completedUtc: string | null;
}

export const JOB_TYPE_NAMES: Record<number, string> = {
  0: "Transcribe",
  1: "Embed",
  2: "Segment",
  3: "DetectNets",
  4: "Summarize",
};

export class ProcessingApi {
  static async getStats(): Promise<ProcessingStatsDto> {
    return (await http.get<ProcessingStatsDto>("/api/v0/processing/stats")).data;
  }

  static async getFailed(limit = 25): Promise<FailedJobDto[]> {
    return (await http.get<FailedJobDto[]>("/api/v0/processing/failed", { params: { limit } })).data;
  }

  static async retry(jobId: number): Promise<void> {
    await http.post(`/api/v0/processing/jobs/${jobId}/retry`);
  }
}
