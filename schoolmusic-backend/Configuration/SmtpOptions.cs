namespace schoolmusic_backend.Configuration;

/// <summary>
/// Ustawienia serwera SMTP do wysyłki wiadomości weryfikacyjnych i resetu hasła.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "SchoolMusic";
}
