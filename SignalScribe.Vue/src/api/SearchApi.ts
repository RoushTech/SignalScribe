import http from "@/api/axios";

export interface SearchHitDto {
  segmentId: number;
  transmissionId: number;
  frequencyHz: number;
  startUtc: string;
  snippet: string;
}

export class SearchApi {
  static async search(q: string, limit = 50): Promise<SearchHitDto[]> {
    const response = await http.get<SearchHitDto[]>("/api/v0/search", { params: { q, limit } });
    return response.data;
  }
}
