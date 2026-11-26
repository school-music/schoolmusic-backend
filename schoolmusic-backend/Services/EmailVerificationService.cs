using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using schoolmusic_backend.Configuration;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services;

public sealed record EmailVerificationResultDto(
    string Message,
    string Email,
    DateTime EmailVerifiedAtUtc
);

/// <summary>
/// Obsługuje przypisywanie i jednorazową weryfikację adresu e-mail dla kont lokalnych.
/// </summary>
public interface IEmailVerificationService
{
    Task<ServiceResult<string>> IssueVerificationForUserAsync(User user, string rawEmail, CancellationToken cancellationToken = default);
    Task<ServiceResult<EmailVerificationResultDto>> VerifyTokenAsync(string rawToken, CancellationToken cancellationToken = default);
    Task ResendVerificationIfEligibleAsync(string identifier, CancellationToken cancellationToken = default);
}

public sealed class EmailVerificationService(
    schoolmusicContext context,
    IEmailSender emailSender,
    IOptions<AuthOptions> authOptions,
    TimeProvider? timeProvider = null,
    ILogger<EmailVerificationService>? logger = null) : IEmailVerificationService
{
    private static readonly EmailAddressAttribute EmailValidator = new();
    private readonly AuthOptions _authOptions = authOptions.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public static string? NormalizeAndValidateEmail(string? rawEmail, out string? validationError)
    {
        validationError = null;
        if (string.IsNullOrWhiteSpace(rawEmail))
        {
            return null;
        }

        var normalized = rawEmail.Trim().ToLowerInvariant();
        if (normalized.Length > 254 || !EmailValidator.IsValid(normalized))
        {
            validationError = "Podany adres e-mail ma nieprawidłowy format.";
            return null;
        }

        return normalized;
    }

    public async Task<ServiceResult<string>> IssueVerificationForUserAsync(
        User user,
        string rawEmail,
        CancellationToken cancellationToken = default)
    {
        if (!_authOptions.EmailLoginEnabled)
        {
            return ServiceResult<string>.BadRequest("Obsługa lokalnych adresów e-mail jest wyłączona.");
        }

        if (string.Equals(user.AuthProvider, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceResult<string>.BadRequest("Adres e-mail konta szkolnego jest zarządzany przez katalog LDAP.");
        }

        var normalized = NormalizeAndValidateEmail(rawEmail, out var error);
        if (normalized == null)
        {
            return ServiceResult<string>.BadRequest(error ?? "Adres e-mail jest wymagany.");
        }

        bool takenByVerifiedUser = await context.Users
            .AnyAsync(u =>
                u.Id != user.Id &&
                u.EmailVerifiedAt != null &&
                u.Email != null &&
                u.Email.ToLower() == normalized,
                cancellationToken);

        if (takenByVerifiedUser)
        {
            return ServiceResult<string>.Conflict("Podany adres e-mail jest już przypisany do innego zweryfikowanego konta.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        user.Email = normalized;
        user.EmailVerifiedAt = null;

        await CreateAndSendTokenAsync(user, normalized, utcNow, cancellationToken);
        return ServiceResult<string>.Ok(normalized, "Wysłano link weryfikacyjny na podany adres e-mail.");
    }

    public async Task<ServiceResult<EmailVerificationResultDto>> VerifyTokenAsync(
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        if (!_authOptions.EmailLoginEnabled)
        {
            return ServiceResult<EmailVerificationResultDto>.BadRequest("Obsługa lokalnych adresów e-mail jest wyłączona.");
        }

        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return ServiceResult<EmailVerificationResultDto>.BadRequest("Token weryfikacyjny jest wymagany.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var hash = TokenHashing.HashToken(rawToken.Trim());

        var tokenRow = await context.EmailVerificationTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (tokenRow == null || tokenRow.UsedAt != null || tokenRow.ExpiresAt <= utcNow)
        {
            return ServiceResult<EmailVerificationResultDto>.BadRequest("Link weryfikacyjny jest nieprawidłowy lub wygasł.");
        }

        if (string.Equals(tokenRow.User.AuthProvider, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceResult<EmailVerificationResultDto>.BadRequest("Konta szkolne korzystają ze szkolnego katalogu tożsamości.");
        }

        var normalizedEmail = tokenRow.Email.Trim().ToLowerInvariant();
        bool alreadyVerifiedElsewhere = await context.Users
            .AnyAsync(u =>
                u.Id != tokenRow.UserId &&
                u.EmailVerifiedAt != null &&
                u.Email != null &&
                u.Email.ToLower() == normalizedEmail,
                cancellationToken);

        if (alreadyVerifiedElsewhere)
        {
            return ServiceResult<EmailVerificationResultDto>.Conflict("Podany adres e-mail został już zweryfikowany dla innego konta.");
        }

        if (context.Database.IsRelational())
        {
            var affected = await context.EmailVerificationTokens
                .Where(t => t.Id == tokenRow.Id && t.UsedAt == null && t.ExpiresAt > utcNow)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.UsedAt, utcNow), cancellationToken);
            if (affected != 1)
            {
                return ServiceResult<EmailVerificationResultDto>.BadRequest("Link weryfikacyjny został już wykorzystany lub wygasł.");
            }
        }
        else
        {
            tokenRow.UsedAt = utcNow;
        }

        var staleUnverifiedUsers = await context.Users
            .Where(u => u.Id != tokenRow.UserId && u.EmailVerifiedAt == null && u.Email != null && u.Email.ToLower() == normalizedEmail)
            .ToListAsync(cancellationToken);
        foreach (var stale in staleUnverifiedUsers)
        {
            stale.Email = null;
        }

        tokenRow.User.Email = normalizedEmail;
        tokenRow.User.EmailVerifiedAt = utcNow;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation())
        {
            return ServiceResult<EmailVerificationResultDto>.Conflict("Podany adres e-mail jest już zweryfikowany dla innego konta.");
        }

        logger?.LogInformation("Zweryfikowano adres e-mail użytkownika {UserId}.", tokenRow.UserId);
        return ServiceResult<EmailVerificationResultDto>.Ok(
            new EmailVerificationResultDto("Adres e-mail został pomyślnie zweryfikowany.", normalizedEmail, utcNow));
    }

    public async Task ResendVerificationIfEligibleAsync(string identifier, CancellationToken cancellationToken = default)
    {
        if (!_authOptions.EmailLoginEnabled || string.IsNullOrWhiteSpace(identifier))
            return;

        var normalized = identifier.Trim().ToLowerInvariant();
        var user = await context.Users.FirstOrDefaultAsync(
            u => u.UserLogin.ToLower() == normalized ||
                 (u.Email != null && u.Email.ToLower() == normalized),
            cancellationToken);

        if (user == null ||
            string.Equals(user.AuthProvider, "ldap", StringComparison.OrdinalIgnoreCase) ||
            user.EmailVerifiedAt != null ||
            string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        bool takenByVerifiedUser = await context.Users.AnyAsync(
            u => u.Id != user.Id && u.EmailVerifiedAt != null && u.Email != null && u.Email.ToLower() == user.Email.ToLower(),
            cancellationToken);
        if (takenByVerifiedUser)
            return;

        await CreateAndSendTokenAsync(user, user.Email.ToLowerInvariant(), _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
    }

    private async Task CreateAndSendTokenAsync(
        User user,
        string normalizedEmail,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var previousUnused = await context.EmailVerificationTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var oldToken in previousUnused)
        {
            oldToken.UsedAt = utcNow;
        }

        var rawToken = TokenHashing.GenerateUrlSafeToken(32);
        var expiresAt = utcNow.AddMinutes(Math.Clamp(_authOptions.EmailVerificationTokenMinutes, 15, 10080));

        context.EmailVerificationTokens.Add(new EmailVerificationToken
        {
            UserId = user.Id,
            Email = normalizedEmail,
            TokenHash = TokenHashing.HashToken(rawToken),
            ExpiresAt = expiresAt,
            CreatedAt = utcNow
        });

        await context.SaveChangesAsync(cancellationToken);

        var url = BuildActionUrl(_authOptions.EmailVerificationUrlTemplate, rawToken);
        var text = $"Potwierdź adres e-mail w SchoolMusic, otwierając jednorazowy link (ważny do {expiresAt:u}): {url}";
        var html = $"<p>Potwierdź adres e-mail w SchoolMusic, klikając jednorazowy link:</p><p><a href=\"{System.Net.WebUtility.HtmlEncode(url)}\">{System.Net.WebUtility.HtmlEncode(url)}</a></p>";

        await emailSender.SendAsync(normalizedEmail, "SchoolMusic — weryfikacja adresu e-mail", text, html, url, cancellationToken);
    }

    private string BuildActionUrl(string template, string rawToken)
    {
        var baseUrl = (_authOptions.PublicBaseUrl ?? "http://localhost:8080").TrimEnd('/');
        return template
            .Replace("{baseUrl}", baseUrl, StringComparison.Ordinal)
            .Replace("{token}", Uri.EscapeDataString(rawToken), StringComparison.Ordinal);
    }
}
