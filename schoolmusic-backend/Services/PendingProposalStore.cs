namespace schoolmusic_backend.Services;

/// <summary>
/// Definiuje klucze Redis używane przez kolejkę propozycji oczekujących na moderację.
/// </summary>
public static class PendingProposalStore
{
    public const string ListKey = "proposals:pending";

    public static string GetDeduplicationKey(int breakId, int songId) =>
        $"proposals:pending:{breakId}:{songId}";
}
