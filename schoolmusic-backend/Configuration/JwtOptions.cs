using System.Text;
using Microsoft.Extensions.Options;

namespace schoolmusic_backend.Configuration;

/// <summary>
/// Konfiguracja tokenów JWT używanych do uwierzytelniania użytkowników.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int ExpirationHours { get; set; } = 12;
}

/// <summary>
/// Sprawdza poprawność konfiguracji JWT przy starcie aplikacji.
/// </summary>
public sealed class JwtOptionsValidator(IWebHostEnvironment environment) : IValidateOptions<JwtOptions>
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "YOUR_SECRET_KEY_HERE",
        "default_jwt_secret_key_development_only",
        "schoolmusic_super_secret_jwt_key_2026_dev!"
    };

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Issuer))
            errors.Add("Jwt:Issuer jest wymagany.");
        if (string.IsNullOrWhiteSpace(options.Audience))
            errors.Add("Jwt:Audience jest wymagany.");
        if (options.ExpirationHours is < 1 or > 24)
            errors.Add("Jwt:ExpirationHours musi mieścić się w zakresie od 1 do 24 godzin.");

        if (string.IsNullOrWhiteSpace(options.Key))
        {
            errors.Add("Jwt:Key jest wymagany.");
        }
        else if (environment.IsProduction())
        {
            if (KnownKeys.Contains(options.Key))
                errors.Add("Jwt:Key nie może używać znanego klucza demonstracyjnego lub wartości domyślnej.");
            if (Encoding.UTF8.GetByteCount(options.Key) < 32)
                errors.Add("Jwt:Key musi mieć co najmniej 32 bajty (256 bitów).");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
