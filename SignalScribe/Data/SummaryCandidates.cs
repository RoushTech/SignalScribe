using Microsoft.EntityFrameworkCore;
using SignalScribe.Data.Models;

namespace SignalScribe.Data;

/// <summary>
/// Which closed sessions are worth an LLM summary.
///
/// The rule is small — a net occurrence always, an ordinary ragchew only if it ran long enough to
/// have anything to say — but *where* it is applied decides whether summaries happen at all. It has
/// to be in the query, not after it: the candidate list is capped so one pass cannot enqueue
/// unbounded work, and a cap applied before the rule means the page fills with sessions that are
/// then all discarded. Those sessions never gain a summary, so they are still candidates on the
/// next pass, and the same rejected page is fetched forever while everything behind it starves.
///
/// Ordering newest-first matters for the same reason: when there is more eligible work than one
/// pass can take, the backlog that gets served is the recent one an operator is actually looking at.
/// </summary>
public static class SummaryCandidates
{
    /// <summary>A ragchew shorter than this has nothing worth narrating; a net is summarised at any length.</summary>
    public const double MinRagchewMinutes = 10;

    /// <inheritdoc cref="MinRagchewMinutes"/>
    public static readonly TimeSpan MinRagchew = TimeSpan.FromMinutes(MinRagchewMinutes);

    /// <summary>The eligibility rule on its own, so it can be reasoned about without a database.</summary>
    public static bool ShouldSummarize(bool isNet, TimeSpan duration) => isNet || duration >= MinRagchew;

    /// <summary>
    /// Sessions ready to summarise: closed, quiet long enough to be finished, carrying at least one
    /// real transcript, and eligible under <see cref="ShouldSummarize"/> — all decided in SQL so the
    /// cap selects work that will actually be done.
    /// </summary>
    public static Task<List<Session>> FetchAsync(
        SignalScribeContext db, DateTime closedBefore, int take, CancellationToken ct) =>
        db.Sessions
            .Where(s => s.Summary == null && s.EndUtc != null && s.EndUtc < closedBefore)
            // Expressed as a shifted end rather than (end - start), which SQLite's provider cannot
            // translate — and a duration filter that silently fell back to client evaluation would
            // be the same bug in a new place, fetching the page before applying the rule.
            .Where(s => s.IsNet || s.EndUtc!.Value.AddMinutes(-MinRagchewMinutes) >= s.StartUtc)
            .Where(s => s.Transmissions.Any(t => t.Segments.Any(seg => seg.Transcript != null && seg.Transcript != "")))
            .OrderByDescending(s => s.EndUtc)
            .Take(take)
            .ToListAsync(ct);
}
