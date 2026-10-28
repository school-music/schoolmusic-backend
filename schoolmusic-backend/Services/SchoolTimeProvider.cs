namespace schoolmusic_backend.Services;

/// <summary>
/// Udostępnia czas lokalny szkoły niezależnie od strefy czasowej systemu operacyjnego lub kontenera.
/// </summary>
public sealed class SchoolTimeProvider(TimeProvider source, TimeZoneInfo schoolTimeZone) : TimeProvider
{
    public override TimeZoneInfo LocalTimeZone => schoolTimeZone;
    public override long TimestampFrequency => source.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => source.GetUtcNow();
    public override long GetTimestamp() => source.GetTimestamp();

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) => source.CreateTimer(callback, state, dueTime, period);

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            throw new InvalidOperationException("School:TimeZone jest wymagany.");

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new InvalidOperationException($"Nie znaleziono szkolnej strefy czasowej '{timeZoneId}'.", ex);
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new InvalidOperationException($"Szkolna strefa czasowa '{timeZoneId}' ma nieprawidłową konfigurację.", ex);
        }
    }
}
