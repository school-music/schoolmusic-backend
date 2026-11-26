using System.Text;
using Microsoft.Extensions.Options;

namespace schoolmusic_backend.Configuration;

/// <summary>
/// Konfiguracja tokenów JWT oraz tokenów odświeżania używanych do uwierzytelniania użytkowników.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int ExpirationHours { get; set; } = 12;
    public int? AccessTokenExpirationMinutes { get; set; }
    public int RefreshTokenExpirationDays { get; set; } = 14;

    public TimeSpan GetAccessTokenLifetime() =>
        AccessTokenExpirationMinutes.HasValue
            ? TimeSpan.FromMinutes(AccessTokenExpirationMinutes.Value)
            : TimeSpan.FromHours(ExpirationHours);

    public TimeSpan GetRefreshTokenLifetime() =>
        TimeSpan.FromDays(RefreshTokenExpirationDays);
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
        "schoolmusic_super_secret_jwt_key_2026_dev!",
        "UZUPELNIJ_LOSOWYM_KLUCZEM_MINIMUM_32_BAJTY"
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
        if (options.AccessTokenExpirationMinutes is < 1 or > 1440)
            errors.Add("Jwt:AccessTokenExpirationMinutes musi mieścić się w zakresie od 1 do 1440 minut.");
        if (options.RefreshTokenExpirationDays is < 1 or > 90)
            errors.Add("Jwt:RefreshTokenExpirationDays musi mieścić się w zakresie od 1 do 90 dni.");

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
