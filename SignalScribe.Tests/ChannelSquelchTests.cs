using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SignalScribe.Contracts;
using SignalScribe.Data;
using SignalScribe.Data.Models;
using Xunit;

namespace SignalScribe.Tests;

/// <summary>
/// Per-channel squelch state: the persisted noise floor, the adaptive/pinned switch, and the
/// CTCSS-or-DCS choice.
/// </summary>
public sealed class ChannelSquelchTests : IDisposable
{
    private readonly SqliteConnection _connection;

    private readonly SignalScribeContext _db;

    public ChannelSquelchTests()
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

    /// <summary>
    /// Adaptive tracking is what every channel was doing before this existed, so the migration must
    /// leave them doing it. Defaulting to off would pin every floor on the band at once.
    /// </summary>
    [Fact]
    public async Task ChannelsAreAdaptiveByDefault()
    {
        _db.Channels.Add(new Channel { FrequencyHz = 146_790_000, Label = "146.790" });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var channel = await _db.Channels.SingleAsync();
        Assert.True(channel.AdaptiveSquelch);
        Assert.Null(channel.NoiseFloorDbfs);
    }

    [Fact]
    public async Task ALearnedFloorSurvivesARoundTrip()
    {
        _db.Channels.Add(new Channel
        {
            FrequencyHz = 146_790_000,
            Label = "146.790",
            NoiseFloorDbfs = -104.2,
            AdaptiveSquelch = false,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var channel = await _db.Channels.SingleAsync();
        Assert.Equal(-104.2, channel.NoiseFloorDbfs);
        Assert.False(channel.AdaptiveSquelch);
    }

    /// <summary>
    /// CTCSS and DCS are alternative systems, never both — a tone beating against the DCS bit clock
    /// produces a Golay-valid phantom code, so a channel carrying both configured is a channel that
    /// will eventually disagree with itself.
    /// </summary>
    [Fact]
    public void SettingATonClearsTheCode()
    {
        var channel = new Channel { DcsCode = 23 };

        channel.SetSquelchTone(146.2, null);

        Assert.Equal(146.2, channel.CtcssToneHz);
        Assert.Null(channel.DcsCode);
    }

    [Fact]
    public void SettingACodeClearsTheTone()
    {
        var channel = new Channel { CtcssToneHz = 146.2 };

        channel.SetSquelchTone(null, 23);

        Assert.Null(channel.CtcssToneHz);
        Assert.Equal(23, channel.DcsCode);
    }

    [Fact]
    public void SettingNeitherIsCarrierSquelch()
    {
        var channel = new Channel { CtcssToneHz = 146.2, DcsCode = 23 };

        channel.SetSquelchTone(null, null);

        Assert.Null(channel.CtcssToneHz);
        Assert.Null(channel.DcsCode);
    }

    [Fact]
    public void AskingForBothKeepsOnlyTheTone()
    {
        // A caller supplying both is confused about the hardware rather than expressing a
        // preference; the tone wins because that is what nearly every repeater uses.
        var channel = new Channel();

        channel.SetSquelchTone(146.2, 23);

        Assert.Equal(146.2, channel.CtcssToneHz);
        Assert.Null(channel.DcsCode);
    }

    [Fact]
    public async Task SquelchStateIsExposedForTheCaptureDaemon()
    {
        _db.Channels.AddRange(
            new Channel { FrequencyHz = 146_790_000, Label = "a", NoiseFloorDbfs = -104.2, AdaptiveSquelch = true },
            new Channel { FrequencyHz = 147_180_000, Label = "b", NoiseFloorDbfs = -99.0, AdaptiveSquelch = false },
            new Channel { FrequencyHz = 145_310_000, Label = "c", Enabled = false });
        await _db.SaveChangesAsync();

        var info = await _db.Channels
            .Where(c => c.Enabled)
            .Select(c => new ChannelSquelchInfo(c.FrequencyHz, c.NoiseFloorDbfs, c.AdaptiveSquelch))
            .ToListAsync();

        Assert.Equal(2, info.Count); // the disabled channel is not capture's concern
        Assert.Contains(info, i => i.FrequencyHz == 147_180_000 && !i.Adaptive && i.NoiseFloorDbfs == -99.0);
    }
}
