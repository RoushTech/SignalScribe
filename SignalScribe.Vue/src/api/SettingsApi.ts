import http from "@/api/axios";

export interface CaptureSettingsDto {
  centerFrequencyHz: number;
  sampleRateHz: number;
  channelSpacingHz: number;
  gainReductionDb: number;
  lnaState: number;
  agcEnabled: boolean;
  squelchOpenDb: number;
  squelchCloseDb: number;
  squelchHangMs: number;
  deviationHz: number;
  monitorLowHz: number;
  monitorHighHz: number;
  deviceSerial: string | null;
}

export interface WorkerSettingsDto {
  whisperModel: string;
  transcriptionPrompt: string;
  summaryModel: string;
  maxJobsPerClaim: number;
  transcriptionThreads: number;
  summaryThreads: number;
  paused: boolean;
  discardRetentionHours: number;
  noSpeechRetentionHours: number;
  transcriptionGatherSeconds: number;
}

export interface AvailableModelsDto {
  whisper: string[];
  summary: string[];
}

export class SettingsApi {
  static async getCapture(): Promise<CaptureSettingsDto> {
    return (await http.get<CaptureSettingsDto>("/api/v0/settings/capture")).data;
  }

  static async putCapture(settings: CaptureSettingsDto): Promise<CaptureSettingsDto> {
    return (await http.put<CaptureSettingsDto>("/api/v0/settings/capture", settings)).data;
  }

  static async getWorkers(): Promise<WorkerSettingsDto> {
    return (await http.get<WorkerSettingsDto>("/api/v0/settings/workers")).data;
  }

  static async putWorkers(settings: WorkerSettingsDto): Promise<WorkerSettingsDto> {
    return (await http.put<WorkerSettingsDto>("/api/v0/settings/workers", settings)).data;
  }

  static async getModels(): Promise<AvailableModelsDto> {
    return (await http.get<AvailableModelsDto>("/api/v0/settings/models")).data;
  }
}
