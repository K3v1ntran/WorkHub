namespace WorkHub.Core.Backup;

/// <summary>
/// Asia/Tokyo calendar days for the daily backup (Phase 9 §6.1). Only UTC from the injected clock is used, so the
/// machine's own time-zone setting never matters.
/// </summary>
public sealed class JstCalendar
{
    /// <summary>Japan has had no daylight saving time since 1951; used only if the system has no Tokyo zone.</summary>
    public static readonly TimeSpan FixedOffset = TimeSpan.FromHours(9);

    private readonly TimeZoneInfo? _zone;

    public JstCalendar(TimeZoneInfo? zone)
    {
        _zone = zone;
    }

    /// <summary>True when neither "Asia/Tokyo" nor "Tokyo Standard Time" resolved and the fixed +09:00 offset is used.</summary>
    public bool UsesFixedOffset => _zone is null;

    /// <summary>Resolves "Asia/Tokyo", then "Tokyo Standard Time", else the fixed +09:00 offset.</summary>
    public static JstCalendar Resolve(Func<string, TimeZoneInfo>? findZone = null)
    {
        findZone ??= TimeZoneInfo.FindSystemTimeZoneById;
        foreach (var id in new[] { "Asia/Tokyo", "Tokyo Standard Time" })
        {
            try
            {
                return new JstCalendar(findZone(id));
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }

        return new JstCalendar(null);
    }

    /// <summary>The instant as Tokyo wall-clock time.</summary>
    public DateTimeOffset ToTokyo(DateTimeOffset instant) =>
        _zone is null ? instant.ToOffset(FixedOffset) : TimeZoneInfo.ConvertTime(instant, _zone);

    /// <summary>The Tokyo calendar day of the instant.</summary>
    public DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(ToTokyo(instant).DateTime);
}
