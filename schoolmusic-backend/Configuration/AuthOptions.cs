namespace schoolmusic_backend.Configuration;

/// <summary>
/// Ustawienia uwierzytelniania lokalnego, logowania e-mailem, weryfikacji adresu i odzyskiwania hasła.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public bool AllowLocalRegistration { get; set; }
    public bool EmailLoginEnabled { get; set; } = true;
    public int PasswordResetTokenMinutes { get; set; } = 15;
    public int EmailVerificationTokenMinutes { get; set; } = 1440;
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";
    public string PasswordResetUrlTemplate { get; set; } = "{baseUrl}/reset-password?token={token}";
    public string EmailVerificationUrlTemplate { get; set; } = "{baseUrl}/verify-email?token={token}";
}
