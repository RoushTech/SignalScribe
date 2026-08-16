using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SignalScribe.Data.Models;

/// <summary>
/// Single-row operator radio config (Id is always 1, seeded by migration). Retune fields
/// (frequency/rate/spacing) rebuild the capture pipeline; gain/AGC/squelch apply live.
/// </summary>
public class CaptureSettings : IEntityTypeConfiguration<CaptureSettings>
{
    public int Id { get; set; }

    public long CenterFrequencyHz { get; set; }

    public long SampleRateHz { get; set; }

    public int ChannelSpacingHz { get; set; }

    /// <summary>sdrplay_api gRdB, 20–59.</summary>
    public int GainReductionDb { get; set; }

    /// <summary>RSP1 LNA state, 0–3.</summary>
    public int LnaState { get; set; }

    public bool AgcEnabled { get; set; }

    public double SquelchOpenDb { get; set; }

    public double SquelchCloseDb { get; set; }

    public int SquelchHangMs { get; set; }

    /// <summary>FM deviation mapped to full-scale audio. ±5 kHz is standard amateur NBFM; use ±2.5 kHz for narrowband channels.</summary>
    public double DeviationHz { get; set; }

    /// <summary>Only these frequencies are gated/recorded. Defaults to the 2m band — the span always
    /// extends past it, and outside the SDR's analog passband levels are numerically unstable.</summary>
    public long MonitorLowHz { get; set; }

    public long MonitorHighHz { get; set; }

    /// <summary>SDRPlay device serial to bind to. Null = first available (the one-device default).</summary>
    public string? DeviceSerial { get; set; }

    public void Configure(EntityTypeBuilder<CaptureSettings> builder)
    {
        builder.Property(s => s.DeviceSerial).HasMaxLength(64);

        builder.HasData(new CaptureSettings
        {
            Id = 1,
            CenterFrequencyHz = 146_000_000,
            SampleRateHz = 6_400_000, // fs/spacing must be a power of two for the channelizer (512 × 12.5 kHz)
            ChannelSpacingHz = 12_500,
            GainReductionDb = 40,
            LnaState = 0,
            AgcEnabled = true,
            SquelchOpenDb = 8,
            SquelchCloseDb = 5,
            SquelchHangMs = 400,
            DeviationHz = 5_000,
            MonitorLowHz = 144_000_000,
            MonitorHighHz = 148_000_000,
        });
    }
}
