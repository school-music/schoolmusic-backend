namespace schoolmusic_backend.Configuration;

/// <summary>
/// Konfiguracja lokalnego silnika audio (LibVLC) w zależności od platformy docelowej.
/// </summary>
public sealed class AudioOptions
{
    public const string SectionName = "Audio";

    /// <summary>
    /// Nazwa modułu wyjścia audio LibVLC (np. "directsound" na Windows, "alsa", "pulse" lub "dummy" w kontenerze).
    /// Pusta wartość wybiera ustawienie domyślne platformy.
    /// </summary>
    public string? LibVlcOutput { get; set; }

    /// <summary>
    /// Dodatkowe argumenty wiersza poleceń przekazywane do konstruktora LibVLC.
    /// </summary>
    public string[] ExtraVlcOptions { get; set; } = [];

    /// <summary>
    /// Katalog docelowy dla wgrywanych plików audio (domyślnie &lt;AppContext.BaseDirectory&gt;/recordings).
    /// </summary>
    public string? RecordingsDirectory { get; set; }
}
