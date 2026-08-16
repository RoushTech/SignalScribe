using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Api.Controllers.Models;
using SignalScribe.Data;

namespace SignalScribe.Api.Controllers;

[ApiController]
[Route("api/v0/search")]
public class SearchController(SignalScribeContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<SearchHitDto>>> Search([FromQuery] string q, [FromQuery] int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Ok(new List<SearchHitDto>());
        }

        limit = Math.Clamp(limit, 1, 200);

        // FTS5 MATCH via FromSql — the one sanctioned raw-SQL query surface (see CLAUDE.md).
        var hits = await db.SegmentSearchHits
            .FromSql($"""
                SELECT s."Id" AS "SegmentId",
                       snippet(segment_fts, 0, '<b>', '</b>', '…', 12) AS "Snippet",
                       bm25(segment_fts) AS "Rank"
                FROM segment_fts
                JOIN "Segments" s ON s."Id" = segment_fts.rowid
                WHERE segment_fts MATCH {q}
                ORDER BY "Rank"
                LIMIT {limit}
                """)
            .ToListAsync();

        var segmentIds = hits.Select(h => h.SegmentId).ToList();
        var meta = await db.Segments
            .Where(s => segmentIds.Contains(s.Id))
            .Select(s => new { s.Id, s.TransmissionId, s.Transmission.StartUtc, s.Transmission.Channel.FrequencyHz })
            .ToDictionaryAsync(s => s.Id);

        return Ok(hits
            .Where(h => meta.ContainsKey(h.SegmentId))
            .Select(h => new SearchHitDto(
                h.SegmentId,
                meta[h.SegmentId].TransmissionId,
                meta[h.SegmentId].FrequencyHz,
                meta[h.SegmentId].StartUtc,
                h.Snippet))
            .ToList());
    }
}
