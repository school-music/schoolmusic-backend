namespace schoolmusic_backend.Configuration;

/// <summary>
/// Ustawienia uwierzytelniania lokalnego, logowania e-mailem, weryfikacji adresu i odzyskiwania hasła.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public const string DefaultPublicBaseUrl = "http://localhost:8080";
    public const string DefaultPasswordResetUrlTemplate = "{baseUrl}/reset-password?token={token}";
    public const string DefaultEmailVerificationUrlTemplate = "{baseUrl}/verify-email?token={token}";

    public bool AllowLocalRegistration { get; set; }
    public bool EmailLoginEnabled { get; set; } = true;
    public int PasswordResetTokenMinutes { get; set; } = 15;
    public int EmailVerificationTokenMinutes { get; set; } = 1440;
    public string PublicBaseUrl { get; set; } = DefaultPublicBaseUrl;
    public string PasswordResetUrlTemplate { get; set; } = DefaultPasswordResetUrlTemplate;
    public string EmailVerificationUrlTemplate { get; set; } = DefaultEmailVerificationUrlTemplate;
    public string? MobilePasswordResetUrlTemplate { get; set; }
    public string? MobileEmailVerificationUrlTemplate { get; set; }

    public string BuildPasswordResetUrl(string rawToken) =>
        BuildActionUrl(PasswordResetUrlTemplate, DefaultPasswordResetUrlTemplate, rawToken);

    public string BuildEmailVerificationUrl(string rawToken) =>
        BuildActionUrl(EmailVerificationUrlTemplate, DefaultEmailVerificationUrlTemplate, rawToken);

    public string? BuildMobilePasswordResetUrl(string rawToken) =>
        BuildOptionalMobileActionUrl(MobilePasswordResetUrlTemplate, rawToken);

    public string? BuildMobileEmailVerificationUrl(string rawToken) =>
        BuildOptionalMobileActionUrl(MobileEmailVerificationUrlTemplate, rawToken);

    private string BuildActionUrl(string? template, string fallbackTemplate, string rawToken)
    {
        var effectiveTemplate = string.IsNullOrWhiteSpace(template) ? fallbackTemplate : template.Trim();
        var baseUrl = (string.IsNullOrWhiteSpace(PublicBaseUrl) ? DefaultPublicBaseUrl : PublicBaseUrl).Trim().TrimEnd('/');

        return effectiveTemplate
            .Replace("{baseUrl}", baseUrl, StringComparison.Ordinal)
            .Replace("{token}", Uri.EscapeDataString(rawToken), StringComparison.Ordinal);
    }

    private string? BuildOptionalMobileActionUrl(string? template, string rawToken)
    {
        if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(rawToken))
            return null;

        var resolved = BuildActionUrl(template, template, rawToken);
        return IsSafeActionUri(resolved) ? resolved : null;
    }

    public static bool IsSafeActionUri(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri))
            return false;

        return !uri.Scheme.Equals("javascript", StringComparison.OrdinalIgnoreCase) &&
               !uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase) &&
               !uri.Scheme.Equals("vbscript", StringComparison.OrdinalIgnoreCase) &&
               !uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase) &&
               !uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase);
    }
}
