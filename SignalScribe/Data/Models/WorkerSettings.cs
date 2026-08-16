using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SignalScribe.Data.Models;

/// <summary>Single-row worker/processing config (Id is always 1, seeded by migration).</summary>
public class WorkerSettings : IEntityTypeConfiguration<WorkerSettings>
{
    public int Id { get; set; }

    /// <summary>Whisper model filename inside the models directory.</summary>
    public string WhisperModel { get; set; } = string.Empty;

    /// <summary>initial_prompt seeded into Whisper — ham vocabulary, local repeater names, known callsigns.</summary>
    public string TranscriptionPrompt { get; set; } = string.Empty;

    /// <summary>LLM model filename inside the models directory.</summary>
    public string SummaryModel { get; set; } = string.Empty;

    /// <summary>
    /// How many jobs a worker leases from the queue per poll. This is a queue-batching knob, not a
    /// CPU knob — claimed jobs run one after another, so raising it only means fewer round-trips to
    /// the host (and more jobs held under lease if the worker dies). Use
    /// <see cref="TranscriptionThreads"/> / <see cref="SummaryThreads"/> to control CPU.
    /// </summary>
    public int MaxJobsPerClaim { get; set; }

    /// <summary>CPU threads for Whisper. 0 = automatic, which leaves one core free for capture.</summary>
    public int TranscriptionThreads { get; set; }

    /// <summary>CPU threads for the summary LLM. 0 = automatic, which leaves one core free for capture.</summary>
    public int SummaryThreads { get; set; }

    /// <summary>Operator pause switch — workers stop claiming; queue accumulates.</summary>
    public bool Paused { get; set; }

    /// <summary>How long rejected clips are kept for review before the daily purge removes them.</summary>
    public int DiscardRetentionHours { get; set; }

    public void Configure(EntityTypeBuilder<WorkerSettings> builder)
    {
        builder.Property(w => w.WhisperModel).HasMaxLength(128);
        builder.Property(w => w.SummaryModel).HasMaxLength(128);
        builder.Property(w => w.TranscriptionPrompt).HasMaxLength(4096);

        builder.HasData(new WorkerSettings
        {
            Id = 1,
            WhisperModel = "ggml-small.en-q5_1.bin",
            TranscriptionPrompt =
                "Amateur radio net. QSL, QRZ, seventy-three, net control, check-in, kerchunk, repeater, simplex, CQ, destinated.",
            SummaryModel = "Qwen2.5-1.5B-Instruct-Q4_K_M.gguf",
            MaxJobsPerClaim = 4,
            TranscriptionThreads = 0,
            SummaryThreads = 0,
            Paused = false,
            DiscardRetentionHours = 24,
        });
    }
}
