using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Capture.Digital.DStar;
using SignalScribe.Contracts;
using SignalScribe.Data;
using SignalScribe.Data.Models;
using SignalScribe.Enums;
using Xunit;

namespace SignalScribe.Tests;

/// <summary>
/// The mode-agnostic header path: a framer reports every field its mode carries, and all of it
/// survives to the operator. On a mode whose voice needs a vocoder we do not have, the header is
/// the entire content of the transmission, so losing fields here loses the transmission.
/// </summary>
public sealed class DigitalHeaderTests : IDisposable
{
    private readonly SqliteConnection _connection;

    private readonly SignalScribeContext _db;

    public DigitalHeaderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new SignalScribeContext(new DbContextOptionsBuilder<SignalScribeContext>()
            .UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void HeaderModelNamesAreGeneratedPerModeAndStayCompatible()
    {
        // Generated rather than listed, so a new framer needs no constant — and D-STAR must keep
        // the exact name rows already in the database were written with.
        Assert.Equal(Segment.DStarHeaderModel, Segment.HeaderModel(DetectedMode.DStar));
        Assert.Equal("signalscribe/ysf-header", Segment.HeaderModel(DetectedMode.Ysf));
        Assert.Equal("signalscribe/dmr-header", Segment.HeaderModel(DetectedMode.Dmr));
    }

    [Theory]
    [InlineData(DetectedMode.DStar)]
    [InlineData(DetectedMode.Ysf)]
    [InlineData(DetectedMode.P25Phase1)]
    public void AnyModesHeaderCountsAsDecodedDataRatherThanSpeech(DetectedMode mode)
    {
        // The channel audit turns on this: a header is decoded fact, and counting it as speech would
        // re-enable exactly the frequencies the audit exists to shut.
        Assert.True(Segment.IsDecodedData(Segment.HeaderModel(mode)));
    }

    [Fact]
    public void SpeechIsNotDecodedData()
    {
        Assert.False(Segment.IsDecodedData("whisper.cpp/small.en-q5_1"));
        Assert.False(Segment.IsDecodedData(null));
    }

    [Fact]
    public async Task EveryFieldSurvivesAStorageRoundTrip()
    {
        var fields = new List<HeaderField>
        {
            new("My call", "KD9ABC"),
            new("My call suffix", "MOBI"),
            new("Your call", "CQCQCQ"),
            new("Repeater in", "W9XYZ  G"),
            new("Repeater out", "W9XYZ  B"),
            new("Call type", "Group call"),
            new("Emergency", "No"),
            new("Flags", "00 00 00"),
        };

        var channel = new Channel { FrequencyHz = 145_310_000, Label = "145.310" };
        _db.Segments.Add(new Segment
        {
            Transmission = new Transmission
            {
                Channel = channel,
                StartUtc = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc),
                AudioPath = "clips/dv.ogg",
                Mode = DetectedMode.DStar,
            },
            StartMs = 0,
            EndMs = 0,
            Transcript = "KD9ABC /MOBI → CQCQCQ via W9XYZ  G → W9XYZ  B",
            TranscriptionModel = Segment.HeaderModel(DetectedMode.DStar),
            Callsign = "KD9ABC",
            HeaderFields = fields,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var stored = await _db.Segments.SingleAsync();
        Assert.Equal(fields, stored.HeaderFields);
        Assert.Equal("KD9ABC", stored.Callsign);
    }

    [Fact]
    public void SpeechSegmentsCarryNoHeaderFields()
    {
        Assert.Null(new Segment { Transcript = "this is KD9ABC" }.HeaderFields);
    }

    /// <summary>
    /// The D-STAR framer must report the whole header, not just who called whom — the routing, the
    /// call type, the emergency flag and the raw flag bytes all arrived CRC-checked and are all the
    /// operator will ever get from a transmission whose audio stays opaque.
    /// </summary>
    [Fact]
    public void TheDStarFramerReportsEveryHeaderField()
    {
        var header = new DStarHeader(
            [0x00, 0x00, 0x00], "W9XYZ  B", "W9XYZ  G", "CQCQCQ", "KD9ABC", "MOBI");

        var decoded = DStarHeaderFields.Describe(header);

        Assert.Equal(DetectedMode.DStar, decoded.Mode);
        Assert.Equal("KD9ABC", decoded.Callsign);
        Assert.Equal("KD9ABC /MOBI → CQCQCQ via W9XYZ  G → W9XYZ  B", decoded.Summary);
        Assert.Equal(
            ["My call", "My call suffix", "Your call", "Repeater in", "Repeater out", "Call type", "Emergency", "Flags"],
            decoded.Fields.Select(f => f.Name));
        Assert.Equal("00 00 00", decoded.Fields.Single(f => f.Name == "Flags").Value);
    }

    [Fact]
    public void AnEmergencyHeaderSaysSoInBothTheSummaryAndTheFields()
    {
        var header = new DStarHeader(
            [0x08, 0x00, 0x00], "W9XYZ  B", "W9XYZ  G", "CQCQCQ", "KD9ABC", "");

        var decoded = DStarHeaderFields.Describe(header);

        Assert.StartsWith("EMERGENCY —", decoded.Summary);
        Assert.Equal("Yes", decoded.Fields.Single(f => f.Name == "Emergency").Value);

        // A station with no suffix must not carry an empty field for it.
        Assert.DoesNotContain(decoded.Fields, f => f.Name == "My call suffix");
    }

    [Fact]
    public void ADirectedCallIsDistinguishedFromAGroupCall()
    {
        var header = new DStarHeader(
            [0x00, 0x00, 0x00], "W9XYZ  B", "W9XYZ  G", "W1AW", "KD9ABC", "");

        Assert.Equal("Directed call", DStarHeaderFields.Describe(header).Fields.Single(f => f.Name == "Call type").Value);
    }
}
