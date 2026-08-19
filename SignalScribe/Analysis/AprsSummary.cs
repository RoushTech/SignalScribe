using System.Globalization;
using System.Text;
using AprsSharp.AprsParser;

namespace SignalScribe.Analysis;

/// <summary>
/// Turns a decoded APRS packet into a line a person can read.
///
/// The stored transcript stays the raw TNC2 frame, because that is the record: it is what every
/// packet tool speaks, and keeping it means the reading below can be improved and re-applied to
/// traffic already captured. This runs on the way out instead, so nothing here is baked into the
/// database and a better summary next month costs no migration and no re-decode.
///
/// Deliberately brief. The point is that "!4221.55N/08750.12W#PHG5130" reads as a place and a
/// comment in a list of transmissions, not that every APRS field is rendered — a station's full
/// history belongs in a map view, which is DireControl's job rather than this one's.
/// </summary>
public static class AprsSummary
{
    /// <summary>
    /// A one-line reading of <paramref name="tnc2"/>, or null when it cannot be parsed or has
    /// nothing worth saying beyond what the raw frame already shows.
    /// </summary>
    public static string? Describe(string? tnc2)
    {
        if (string.IsNullOrWhiteSpace(tnc2))
        {
            return null;
        }

        AprsSharp.AprsParser.Packet packet;
        try
        {
            packet = new AprsSharp.AprsParser.Packet(tnc2);
        }
        catch (Exception)
        {
            // The APRS information field is a loose, much-abused format and the parser throws on
            // plenty of real traffic. A frame we cannot read is not an error — the raw TNC2 line is
            // still shown, and still searchable.
            return null;
        }

        return packet.InfoField switch
        {
            MessageInfo message => DescribeMessage(message),
            StatusInfo status => Trim(status.Comment),
            WeatherInfo weather => Join(DescribePosition(weather.Position), DescribeWeather(weather), Trim(weather.Comment)),
            PositionlessWeatherInfo weather => Join(DescribeWeather(weather), Trim(weather.Comment)),
            MicEInfo micE => Join(DescribePosition(micE.Position), Trim(micE.Comment)),
            ObjectInfo obj => Join(DescribePosition(obj.Position), Trim(obj.Comment)),
            ItemInfo item => Join(DescribePosition(item.Position), Trim(item.Comment)),
            PositionInfo position => Join(DescribePosition(position.Position), Trim(position.Comment)),
            _ => null,
        };
    }

    private static string? DescribeMessage(MessageInfo message)
    {
        var to = Trim(message.Addressee);
        var content = Trim(message.Content);
        if (content is null)
        {
            return to is null ? null : $"message to {to}";
        }

        return to is null ? content : $"to {to}: {content}";
    }

    private static string? DescribePosition(Position? position)
    {
        if (position?.Coordinates is not { } coordinates
            || double.IsNaN(coordinates.Latitude)
            || double.IsNaN(coordinates.Longitude))
        {
            return null;
        }

        // Four decimal places is about 11 m — finer than APRS's own uncompressed resolution, and
        // enough that two positions from the same station are distinguishable at a glance.
        var latitude = Math.Abs(coordinates.Latitude).ToString("0.####", CultureInfo.InvariantCulture);
        var longitude = Math.Abs(coordinates.Longitude).ToString("0.####", CultureInfo.InvariantCulture);
        return $"{latitude}°{(coordinates.Latitude >= 0 ? 'N' : 'S')} {longitude}°{(coordinates.Longitude >= 0 ? 'E' : 'W')}";
    }

    private static string? DescribeWeather(WeatherInfo weather) =>
        DescribeWeather(weather.Temperature, weather.WindSpeed, weather.WindGust, weather.WindDirection, weather.Humidity);

    private static string? DescribeWeather(PositionlessWeatherInfo weather) =>
        DescribeWeather(weather.Temperature, weather.WindSpeed, weather.WindGust, weather.WindDirection, weather.Humidity);

    private static string? DescribeWeather(int? temperatureF, int? windSpeed, int? windGust, int? windDirection, int? humidity)
    {
        var parts = new List<string>(4);
        if (temperatureF is { } temperature)
        {
            parts.Add($"{temperature}°F");
        }

        if (windSpeed is { } wind and > 0)
        {
            var gust = windGust is { } g and > 0 ? $" gusting {g}" : string.Empty;
            var from = windDirection is { } direction ? $" from {direction}°" : string.Empty;
            parts.Add($"wind {wind} mph{gust}{from}");
        }

        if (humidity is { } rh and > 0)
        {
            parts.Add($"{rh}% humidity");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? Join(params string?[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is null)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(" — ");
            }

            builder.Append(part);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
