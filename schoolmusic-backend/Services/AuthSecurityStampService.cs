using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using schoolmusic_backend.Models;
using StackExchange.Redis;

namespace schoolmusic_backend.Services;

public sealed record UserSecurityState(
    bool Exists,
    bool IsBanned,
    int TokenVersion,
    string RoleName
);

/// <summary>
/// Przechowuje w Redis (z fallbackiem do pamięci i DB) bieżący stan bezpieczeństwa użytkownika
/// oraz listę unieważnionych identyfikatorów JWT (<c>jti</c>).
/// </summary>
public interface IAuthSecurityStampService
{
    Task<UserSecurityState> GetUserSecurityStateAsync(int userId, CancellationToken cancellationToken = default);
    Task InvalidateUserAsync(int userId, CancellationToken cancellationToken = default);
    Task RevokeAccessTokenAsync(string jti, TimeSpan remainingLifetime, CancellationToken cancellationToken = default);
    Task<bool> IsAccessTokenRevokedAsync(string jti, CancellationToken cancellationToken = default);
}

public sealed class AuthSecurityStampService : IAuthSecurityStampService
{
    private static readonly TimeSpan SecurityCacheTtl = TimeSpan.FromSeconds(30);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionMultiplexer? _redis;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<AuthSecurityStampService> _logger;

    public AuthSecurityStampService(
        IServiceScopeFactory scopeFactory,
        IMemoryCache memoryCache,
        ILogger<AuthSecurityStampService> logger,
        IConnectionMultiplexer? redis = null)
    {
        _scopeFactory = scopeFactory;
        _memoryCache = memoryCache;
        _logger = logger;
        _redis = redis;
    }

    private static string UserCacheKey(int userId) => $"auth:security:{userId}";
    private static string RevokedJtiKey(string jti) => $"auth:revoked:jti:{jti}";

    public async Task<UserSecurityState> GetUserSecurityStateAsync(int userId, CancellationToken cancellationToken = default)
    {
        var key = UserCacheKey(userId);
        if (_redis is { IsConnected: true })
        {
            try
            {
                var db = _redis.GetDatabase();
                var raw = await db.StringGetAsync(key);
                if (raw.HasValue)
                {
                    var cached = JsonSerializer.Deserialize<UserSecurityState>(raw.ToString());
                    if (cached != null)
                        return cached;
                }
            }
            catch (RedisException ex)
            {
                _logger.LogWarning(ex, "Nie udało się odczytać stanu bezpieczeństwa użytkownika {UserId} z Redis.", userId);
            }
        }
        else if (_memoryCache.TryGetValue<UserSecurityState>(key, out var memCached) && memCached != null)
        {
            return memCached;
        }

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();

        var userSnapshot = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.TokenVersion,
                RoleName = u.Rank.Rank1
            })
            .FirstOrDefaultAsync(cancellationToken);

        UserSecurityState state;
        if (userSnapshot == null)
        {
            state = new UserSecurityState(false, false, 0, "user");
        }
        else
        {
            var isBanned = await context.BannedUsers
                .AsNoTracking()
                .AnyAsync(b => b.UserId == userId, cancellationToken);

            state = new UserSecurityState(
                Exists: true,
                IsBanned: isBanned,
                TokenVersion: userSnapshot.TokenVersion,
                RoleName: string.IsNullOrWhiteSpace(userSnapshot.RoleName) ? "user" : userSnapshot.RoleName);
        }

        if (_redis is { IsConnected: true })
        {
            try
            {
                await _redis.GetDatabase().StringSetAsync(
                    key,
                    JsonSerializer.Serialize(state),
                    (TimeSpan?)SecurityCacheTtl,
                    When.Always);
            }
            catch (RedisException ex)
            {
                _logger.LogWarning(ex, "Nie udało się zapisać stanu bezpieczeństwa użytkownika {UserId} w Redis.", userId);
            }
        }
        else
        {
            _memoryCache.Set(key, state, SecurityCacheTtl);
        }

        return state;
    }

    public async Task InvalidateUserAsync(int userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = UserCacheKey(userId);
        _memoryCache.Remove(key);

        if (_redis is { IsConnected: true })
        {
            try
            {
                await _redis.GetDatabase().KeyDeleteAsync(key);
            }
            catch (RedisException ex)
            {
                _logger.LogWarning(ex, "Nie udało się usunąć cache bezpieczeństwa użytkownika {UserId} z Redis.", userId);
            }
        }
    }

    public async Task RevokeAccessTokenAsync(string jti, TimeSpan remainingLifetime, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jti))
            return;

        cancellationToken.ThrowIfCancellationRequested();
        var ttl = remainingLifetime > TimeSpan.Zero ? remainingLifetime : TimeSpan.FromMinutes(1);
        var key = RevokedJtiKey(jti);

        _memoryCache.Set(key, true, ttl);

        if (_redis is { IsConnected: true })
        {
            try
            {
                await _redis.GetDatabase().StringSetAsync(key, "1", (TimeSpan?)ttl, When.Always);
            }
            catch (RedisException ex)
            {
                _logger.LogWarning(ex, "Nie udało się zapisać unieważnionego JTI w Redis.");
            }
        }
    }

    public async Task<bool> IsAccessTokenRevokedAsync(string jti, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jti))
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        var key = RevokedJtiKey(jti);

        if (_memoryCache.TryGetValue<bool>(key, out var revokedInMemory) && revokedInMemory)
            return true;

        if (_redis is { IsConnected: true })
        {
            try
            {
                return await _redis.GetDatabase().KeyExistsAsync(key);
            }
            catch (RedisException ex)
            {
                _logger.LogWarning(ex, "Nie udało się sprawdzić unieważnienia JTI w Redis.");
            }
        }

        return false;
    }
}
