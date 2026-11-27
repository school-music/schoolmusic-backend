using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using schoolmusic_backend.Configuration;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services;

public sealed record PasswordRecoveryRequestResponseDto(
    string Message,
    string? SchoolIdentityProviderUrl,
    string SchoolGuidance
);

public sealed record AdminIssuedRecoveryDto(
    int UserId,
    string Username,
    string ResetToken,
    string ResetUrl,
    DateTime ExpiresAtUtc
);

/// <summary>
/// Obsługuje jednorazowe, krótkotrwałe tokeny odzyskiwania hasła dla zweryfikowanych kont lokalnych
/// oraz kierowanie kont szkolnych LDAP do dostawcy tożsamości szkoły.
/// </summary>
public interface IPasswordRecoveryService
{
    Task<PasswordRecoveryRequestResponseDto> RequestRecoveryAsync(string identifier, CancellationToken cancellationToken = default);
    Task<ServiceResult<string>> ConfirmRecoveryAsync(string rawToken, string newPassword, CancellationToken cancellationToken = default);
    Task<ServiceResult<AdminIssuedRecoveryDto>> IssueAdminAssistedRecoveryAsync(int targetUserId, int callerUserId, int callerRankId, CancellationToken cancellationToken = default);
    PasswordRecoveryRequestResponseDto GetSchoolProviderGuidance();
}

public sealed class PasswordRecoveryService(
    schoolmusicContext context,
    IEmailSender emailSender,
    ISessionTokenService sessionTokenService,
    IAuthSecurityStampService securityStampService,
    IOptions<AuthOptions> authOptions,
    IConfiguration configuration,
    TimeProvider? timeProvider = null,
    ILogger<PasswordRecoveryService>? logger = null) : IPasswordRecoveryService
{
    private const string UniformRequestMessage =
        "Jeśli konto lokalne posiada zweryfikowany adres e-mail, wysłano jednorazowy link do resetu hasła. Konta bez zweryfikowanego adresu odzyskuje administrator, a konta szkolne korzystają ze szkolnego dostawcy tożsamości.";

    private readonly AuthOptions _authOptions = authOptions.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public PasswordRecoveryRequestResponseDto GetSchoolProviderGuidance()
    {
        var schoolUrl = configuration["Ldap:PasswordResetUrl"];
        var guidance = configuration["Ldap:PasswordResetGuidance"]
            ?? "Hasło do konta szkolnego (LDAP / Active Directory) resetuje się u szkolnego dostawcy tożsamości lub u administratora IT szkoły.";

        return new PasswordRecoveryRequestResponseDto(
            Message: UniformRequestMessage,
            SchoolIdentityProviderUrl: string.IsNullOrWhiteSpace(schoolUrl) ? null : schoolUrl.Trim(),
            SchoolGuidance: guidance);
    }

    public async Task<PasswordRecoveryRequestResponseDto> RequestRecoveryAsync(
        string identifier,
        CancellationToken cancellationToken = default)
    {
        var response = GetSchoolProviderGuidance();
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return response;
        }

        var trimmed = identifier.Trim();
        var lower = trimmed.ToLowerInvariant();
        var isEmailInput = trimmed.Contains('@');

        User? user = await context.Users
            .FirstOrDefaultAsync(u => u.UserLogin.ToLower() == lower, cancellationToken);

        if (user == null && isEmailInput && _authOptions.EmailLoginEnabled)
        {
            user = await context.Users.FirstOrDefaultAsync(
                u => u.Email != null && u.EmailVerifiedAt != null && u.Email.ToLower() == lower,
                cancellationToken);
        }

        if (user == null)
        {
            logger?.LogInformation("Zakończono żądanie odzyskiwania hasła jednolitą odpowiedzią (brak dopasowanego konta).");
            return response;
        }

        if (string.Equals(user.AuthProvider, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogInformation(
                "Żądanie odzyskiwania hasła dla konta szkolnego {UserId} przekierowano do szkolnego dostawcy tożsamości.",
                user.Id);
            return response;
        }

        if (!_authOptions.EmailLoginEnabled || user.EmailVerifiedAt == null || string.IsNullOrWhiteSpace(user.Email))
        {
            logger?.LogInformation(
                "Konto lokalne {UserId} nie ma zweryfikowanego adresu e-mail — wymagane odzyskiwanie z pomocą administratora.",
                user.Id);
            return response;
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var (_, rawToken, expiresAt, resetUrl) = await CreateResetTokenAsync(user.Id, utcNow, cancellationToken);

        var text = $"Aby ustawić nowe hasło w SchoolMusic, otwórz jednorazowy link (ważny do {expiresAt:u}): {resetUrl}";
        var html = $"<p>Aby ustawić nowe hasło w SchoolMusic, kliknij jednorazowy link (ważny przez {Math.Clamp(_authOptions.PasswordResetTokenMinutes, 5, 60)} min):</p><p><a href=\"{System.Net.WebUtility.HtmlEncode(resetUrl)}\">{System.Net.WebUtility.HtmlEncode(resetUrl)}</a></p>";

        await emailSender.SendAsync(user.Email, "SchoolMusic — odzyskiwanie hasła", text, html, resetUrl, cancellationToken);
        logger?.LogInformation("Wysłano jednorazowy token resetu hasła dla konta lokalnego {UserId}.", user.Id);

        _ = rawToken; // raw token pozostaje wyłącznie w linku URL wysłanym e-mailem
        return response;
    }

    public async Task<ServiceResult<string>> ConfirmRecoveryAsync(
        string rawToken,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return ServiceResult<string>.BadRequest("Jednorazowy dowód resetu hasła jest wymagany.");
        }

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            return ServiceResult<string>.BadRequest("Nowe hasło musi mieć co najmniej 8 znaków.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var hash = TokenHashing.HashToken(rawToken.Trim());

        var tokenRow = await context.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (tokenRow == null || tokenRow.UsedAt != null || tokenRow.ExpiresAt <= utcNow)
        {
            return ServiceResult<string>.BadRequest("Link resetu hasła jest nieprawidłowy lub wygasł.");
        }

        if (string.Equals(tokenRow.User.AuthProvider, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceResult<string>.BadRequest("Konta szkolne korzystają ze szkolnej procedury resetu hasła.");
        }

        if (context.Database.IsRelational())
        {
            var affected = await context.PasswordResetTokens
                .Where(t => t.Id == tokenRow.Id && t.UsedAt == null && t.ExpiresAt > utcNow)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.UsedAt, utcNow), cancellationToken);
            if (affected != 1)
            {
                return ServiceResult<string>.BadRequest("Link resetu hasła został już wykorzystany lub wygasł.");
            }
        }
        else
        {
            tokenRow.UsedAt = utcNow;
        }

        var user = tokenRow.User;
        user.UserPassword = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.PasswordChangedAt = utcNow;
        await context.SaveChangesAsync(cancellationToken);

        await sessionTokenService.RevokeAllUserSessionsAsync(user, cancellationToken);
        await securityStampService.InvalidateUserAsync(user.Id, cancellationToken);

        logger?.LogInformation("Zakończono reset hasła dla użytkownika {UserId}.", user.Id);
        return ServiceResult<string>.Ok("Hasło zostało pomyślnie zmienione. Zaloguj się nowym hasłem.");
    }

    public async Task<ServiceResult<AdminIssuedRecoveryDto>> IssueAdminAssistedRecoveryAsync(
        int targetUserId,
        int callerUserId,
        int callerRankId,
        CancellationToken cancellationToken = default)
    {
        if (targetUserId == callerUserId)
        {
            return ServiceResult<AdminIssuedRecoveryDto>.BadRequest("Nie możesz wystawić administratorowego resetu dla własnego konta.");
        }

        var targetUser = await context.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken);
        if (targetUser == null)
        {
            return ServiceResult<AdminIssuedRecoveryDto>.NotFound("Nie znaleziono użytkownika o podanym identyfikatorze.");
        }

        if (targetUser.RankId > callerRankId)
        {
            return ServiceResult<AdminIssuedRecoveryDto>.Forbidden("Nie masz uprawnień do resetowania hasła użytkownika o wyższej randze.");
        }

        if (string.Equals(targetUser.AuthProvider, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceResult<AdminIssuedRecoveryDto>.BadRequest("Konta szkolne (LDAP) muszą resetować hasło w szkolnym systemie tożsamości.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var (_, rawToken, expiresAt, resetUrl) = await CreateResetTokenAsync(targetUser.Id, utcNow, cancellationToken);
        await sessionTokenService.RevokeAllUserSessionsAsync(targetUser, cancellationToken);

        logger?.LogInformation(
            "Administrator {AdminUserId} wystawił jednorazowy link odzyskiwania hasła dla konta lokalnego {TargetUserId}.",
            callerUserId,
            targetUser.Id);

        return ServiceResult<AdminIssuedRecoveryDto>.Ok(new AdminIssuedRecoveryDto(
            targetUser.Id,
            targetUser.UserLogin,
            rawToken,
            resetUrl,
            expiresAt));
    }

    private async Task<(PasswordResetToken Entity, string RawToken, DateTime ExpiresAt, string ResetUrl)> CreateResetTokenAsync(
        int userId,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var previousUnused = await context.PasswordResetTokens
            .Where(t => t.UserId == userId && t.UsedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var oldToken in previousUnused)
        {
            oldToken.UsedAt = utcNow;
        }

        var rawToken = TokenHashing.GenerateUrlSafeToken(32);
        var expiresAt = utcNow.AddMinutes(Math.Clamp(_authOptions.PasswordResetTokenMinutes, 5, 60));

        var entity = new PasswordResetToken
        {
            UserId = userId,
            TokenHash = TokenHashing.HashToken(rawToken),
            ExpiresAt = expiresAt,
            CreatedAt = utcNow
        };
        context.PasswordResetTokens.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        var resetUrl = _authOptions.BuildPasswordResetUrl(rawToken);

        return (entity, rawToken, expiresAt, resetUrl);
    }
}
