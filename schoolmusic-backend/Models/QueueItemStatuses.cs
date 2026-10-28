namespace schoolmusic_backend.Models;

/// <summary>
/// Definiuje dozwolone stany pozycji kolejki oraz jednokierunkowe przejścia pomiędzy nimi.
/// </summary>
public static class QueueItemStatuses
{
    public const string Queued = "queued";
    public const string Playing = "playing";
    public const string Played = "played";
    public const string Skipped = "skipped";

    public static bool TryTransition(QueueItem item, string targetStatus)
    {
        if (item.Status == targetStatus)
            return false;

        var allowed = item.Status switch
        {
            Queued => targetStatus is Playing or Skipped,
            Playing => targetStatus is Played or Skipped,
            _ => false
        };
        if (!allowed)
            return false;

        item.Status = targetStatus;
        return true;
    }
}
