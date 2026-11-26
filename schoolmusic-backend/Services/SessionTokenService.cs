using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using schoolmusic_backend.Configuration;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services;

public sealed record IssuedSessionTokens(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc
);

/// <summary>
/// Zarządza cyklem życia tokenów odświeżania sesji (wydawanie, rotacja, wykrywanie ponownego użycia i unieważnianie).
/// </summary>
public interface ISessionTokenService
{
    Task<IssuedSessionTokens> CreateSessionAsync(User user, bool isLdap, CancellationToken cancellationToken = default);
    Task<ServiceResult<RefreshSessionResult>> RefreshSessionAsync(string rawRefreshToken, CancellationToken cancellationToken = default);
    Task RevokeRefreshTokenAsync(string rawRefreshToken, int userId, CancellationToken cancellationToken = default);
    Task RevokeAllUserSessionsAsync(User user, CancellationToken cancellationToken = default);
}

public sealed record RefreshSessionResult(User User, bool IsLdap, IssuedSessionTokens Tokens);

public sealed class SessionTokenService(
    schoolmusicContext context,
    IJwtTokenService jwtTokenService,
    IAuthSecurityStampService securityStampService,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider? timeProvider = null,
    ILogger<SessionTokenService>? logger = null) : ISessionTokenService
{
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IssuedSessionTokens> CreateSessionAsync(User user, bool isLdap, CancellationToken cancellationToken = default)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var rawRefreshToken = TokenHashing.GenerateUrlSafeToken(32);
        var refreshExpiresAt = utcNow.Add(_jwtOptions.GetRefreshTokenLifetime());

        var entity = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenHashing.HashToken(rawRefreshToken),
            FamilyId = Guid.NewGuid().ToString(),
            ExpiresAt = refreshExpiresAt,
            CreatedAt = utcNow
        };

        context.RefreshTokens.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        var access = jwtTokenService.IssueAccessToken(user, isLdap);
        return new IssuedSessionTokens(access.Token, access.ExpiresAtUtc, rawRefreshToken, refreshExpiresAt);
    }

    public async Task<ServiceResult<RefreshSessionResult>> RefreshSessionAsync(string rawRefreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return ServiceResult<RefreshSessionResult>.Unauthorized("Token odświeżania jest wymagany.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var hash = TokenHashing.HashToken(rawRefreshToken.Trim());

        var storedToken = await context.RefreshTokens
            .Include(rt => rt.User)
                .ThenInclude(u => u.Rank)
            .FirstOrDefaultAsync(rt => rt.TokenHash == hash, cancellationToken);

        if (storedToken == null)
        {
            return ServiceResult<RefreshSessionResult>.Unauthorized("Nieprawidłowy token odświeżania.");
        }

        var user = storedToken.User;

        // Wykrycie próby ponownego użycia już zrotowanego lub unieważnionego tokenu -> unieważniamy całą rodzinę i sesje.
        if (storedToken.RevokedAt != null || storedToken.ReplacedById != null)
        {
            logger?.LogWarning("Wykryto ponowne użycie unieważnionego refresh tokenu użytkownika {UserId}.", user.Id);
            await RevokeFamilyAndBumpVersionAsync(user, storedToken.FamilyId, utcNow, cancellationToken);
            return ServiceResult<RefreshSessionResult>.Unauthorized("Sesja wygasła lub została unieważniona. Zaloguj się ponownie.");
        }

        if (storedToken.ExpiresAt <= utcNow)
        {
            storedToken.RevokedAt = utcNow;
            await context.SaveChangesAsync(cancellationToken);
            return ServiceResult<RefreshSessionResult>.Unauthorized("Token odświeżania wygasł. Zaloguj się ponownie.");
        }

        bool isBanned = await context.BannedUsers.AnyAsync(b => b.UserId == user.Id, cancellationToken);
        if (isBanned)
        {
            await RevokeAllUserSessionsAsync(user, cancellationToken);
            return ServiceResult<RefreshSessionResult>.Forbidden("Twoje konto zostało zablokowane przez moderatora.");
        }

        var newRawRefresh = TokenHashing.GenerateUrlSafeToken(32);
        var newExpiresAt = utcNow.Add(_jwtOptions.GetRefreshTokenLifetime());
        var replacement = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenHashing.HashToken(newRawRefresh),
            FamilyId = storedToken.FamilyId,
            ExpiresAt = newExpiresAt,
            CreatedAt = utcNow
        };

        context.RefreshTokens.Add(replacement);
        await context.SaveChangesAsync(cancellationToken);

        storedToken.RevokedAt = utcNow;
        storedToken.ReplacedById = replacement.Id;
        await context.SaveChangesAsync(cancellationToken);

        var isLdap = string.Equals(user.AuthProvider, "ldap", StringComparison.OrdinalIgnoreCase);
        var access = jwtTokenService.IssueAccessToken(user, isLdap);

        return ServiceResult<RefreshSessionResult>.Ok(new RefreshSessionResult(
            user,
            isLdap,
            new IssuedSessionTokens(access.Token, access.ExpiresAtUtc, newRawRefresh, newExpiresAt)));
    }

    public async Task RevokeRefreshTokenAsync(string rawRefreshToken, int userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
            return;

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var hash = TokenHashing.HashToken(rawRefreshToken.Trim());

        var token = await context.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == hash && rt.UserId == userId, cancellationToken);

        if (token != null && token.RevokedAt == null)
        {
            token.RevokedAt = utcNow;
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RevokeAllUserSessionsAsync(User user, CancellationToken cancellationToken = default)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        user.TokenVersion += 1;

        var activeTokens = await context.RefreshTokens
            .Where(rt => rt.UserId == user.Id && rt.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in activeTokens)
        {
            token.RevokedAt = utcNow;
        }

        await context.SaveChangesAsync(cancellationToken);
        await securityStampService.InvalidateUserAsync(user.Id, cancellationToken);
    }

    private async Task RevokeFamilyAndBumpVersionAsync(User user, string familyId, DateTime utcNow, CancellationToken cancellationToken)
    {
        user.TokenVersion += 1;
        var familyTokens = await context.RefreshTokens
            .Where(rt => rt.FamilyId == familyId && rt.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in familyTokens)
        {
            token.RevokedAt = utcNow;
        }

        await context.SaveChangesAsync(cancellationToken);
        await securityStampService.InvalidateUserAsync(user.Id, cancellationToken);
    }
}
