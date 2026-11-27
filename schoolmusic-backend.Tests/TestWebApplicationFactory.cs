using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;
using StackExchange.Redis;

namespace schoolmusic_backend.Tests;

/// <summary>
/// Sterowalny zegar testowy ze szkolną strefą czasową Europe/Warsaw.
/// </summary>
public sealed class AdjustableTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    private readonly TimeZoneInfo _timeZone;

    public AdjustableTimeProvider(DateTimeOffset? initialUtcNow = null, string timeZoneId = "Europe/Warsaw")
    {
        _timeZone = SchoolTimeProvider.ResolveTimeZone(timeZoneId);
        _utcNow = initialUtcNow ?? DateTimeOffset.UtcNow;
    }

    public override TimeZoneInfo LocalTimeZone => _timeZone;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;

    public void SetLocalTime(DateTime localDateTime)
    {
        var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        var offset = _timeZone.GetUtcOffset(unspecified);
        _utcNow = new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
}

/// <summary>
/// Lekka imitacja magazynu Redis działająca w pamięci procesu na potrzeby testów integracyjnych.
/// </summary>
public sealed class InMemoryRedisState
{
    public ConcurrentDictionary<string, RedisValue> Strings { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, List<RedisValue>> Lists { get; } = new(StringComparer.Ordinal);
    public Mock<IConnectionMultiplexer> MultiplexerMock { get; } = new();
    public Mock<IDatabase> DatabaseMock { get; } = new();

    public InMemoryRedisState()
    {
        MultiplexerMock.SetupGet(m => m.IsConnected).Returns(true);
        MultiplexerMock
            .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>()))
            .Returns(DatabaseMock.Object);

        DatabaseMock
            .Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, CommandFlags _) =>
                Strings.TryGetValue(key.ToString(), out var value) ? value : RedisValue.Null);

        DatabaseMock
            .Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue value, TimeSpan? _, bool _, When when, CommandFlags _) =>
                SetString(key.ToString(), value, when));

        DatabaseMock
            .Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue value, Expiration _, ValueCondition condition, CommandFlags _) =>
                SetString(key.ToString(), value, condition.Equals((ValueCondition)When.NotExists) ? When.NotExists : When.Always));

        DatabaseMock
            .Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, CommandFlags flags) =>
                Strings.TryRemove(key.ToString(), out _) || Lists.TryRemove(key.ToString(), out _));

        DatabaseMock
            .Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, CommandFlags _) =>
                Strings.ContainsKey(key.ToString()) || Lists.ContainsKey(key.ToString()));

        DatabaseMock
            .Setup(d => d.ListRangeAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, long _, long _, CommandFlags _) =>
            {
                if (!Lists.TryGetValue(key.ToString(), out var list))
                    return [];
                lock (list)
                {
                    return list.ToArray();
                }
            });

        DatabaseMock
            .Setup(d => d.ListRightPushAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue value, When _, CommandFlags _) =>
            {
                var list = Lists.GetOrAdd(key.ToString(), _ => []);
                lock (list)
                {
                    list.Add(value);
                    return list.Count;
                }
            });

        DatabaseMock
            .Setup(d => d.ListRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue value, long count, CommandFlags _) =>
            {
                if (!Lists.TryGetValue(key.ToString(), out var list))
                    return 0;
                lock (list)
                {
                    long removed = 0;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i] == value)
                        {
                            list.RemoveAt(i);
                            removed++;
                            if (count > 0 && removed >= count)
                                break;
                        }
                    }
                    return removed;
                }
            });
    }

    private bool SetString(string key, RedisValue value, When when)
    {
        if (when == When.NotExists)
            return Strings.TryAdd(key, value);

        Strings[key] = value;
        return true;
    }
}

/// <summary>
/// Wspólna fabryka testowego hosta HTTP oparta o InMemory DB, fałszywy Redis oraz lokalny sink e-mail.
/// </summary>
public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public string DatabaseName { get; } = $"SchoolMusicIntegration_{Guid.NewGuid():N}";
    public AdjustableTimeProvider TimeProviderInstance { get; } = new();
    public InMemoryRedisState Redis { get; } = new();
    public Mock<ILdapService> LdapMock { get; } = new();
    public Mock<ISongSourceService> SongSourceMock { get; } = new();
    public Mock<IAudioPlayerService> AudioPlayerMock { get; } = new();

    public int AuthLoginPermitLimit { get; init; } = 100;
    public int AuthRecoveryPermitLimit { get; init; } = 100;
    public IReadOnlyDictionary<string, string?>? ExtraConfiguration { get; init; }

    public TestWebApplicationFactory()
    {
        LdapMock.SetupGet(l => l.IsEnabled).Returns(true);
        LdapMock.SetupGet(l => l.IsSimulatedMode).Returns(false);
        LdapMock.Setup(l => l.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LdapAuthResult.Fail("Niepoprawny login lub hasło."));

        SongSourceMock.SetupGet(s => s.IsConfigured).Returns(true);
        AudioPlayerMock.Setup(a => a.GetStatus()).Returns(
            new PlayerStatusDto(false, false, 50, false, "None", null, null, 0, 0));
        AudioPlayerMock.Setup(a => a.PauseAsync()).ReturnsAsync(
            ServiceResult<PlayerStatusDto>.Ok(new PlayerStatusDto(false, true, 50, false, "None", null, null, 0, 0)));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:database"] = "Server=localhost;Database=test;Uid=test;Pwd=test;",
                ["ConnectionStrings:redis"] = "localhost:6379",
                ["Jwt:Key"] = "integration-test-jwt-signing-key-at-least-32-chars!",
                ["Jwt:Issuer"] = "SchoolMusicTestIssuer",
                ["Jwt:Audience"] = "SchoolMusicTestAudience",
                ["Jwt:AccessTokenExpirationMinutes"] = "15",
                ["Jwt:RefreshTokenExpirationDays"] = "7",
                ["Auth:AllowLocalRegistration"] = "true",
                ["Auth:EmailLoginEnabled"] = "true",
                ["Auth:EmailVerificationTokenMinutes"] = "60",
                ["Auth:PasswordResetTokenMinutes"] = "30",
                ["Auth:PublicBaseUrl"] = "https://schoolmusic.test",
                ["Ldap:Enabled"] = "true",
                ["Ldap:Server"] = "ldap.school.test",
                ["Ldap:PasswordResetUrl"] = "https://idp.school.test/reset-password",
                ["RateLimiting:AuthLoginPermitLimit"] = AuthLoginPermitLimit.ToString(),
                ["RateLimiting:AuthLoginWindowSeconds"] = "60",
                ["RateLimiting:AuthRecoveryPermitLimit"] = AuthRecoveryPermitLimit.ToString(),
                ["RateLimiting:AuthRecoveryWindowMinutes"] = "15",
                ["Features:SwaggerEnabled"] = "true",
                ["Scheduler:Enabled"] = "false",
                ["Audio:LibVlcOutput"] = "dummy",
                ["School:TimeZone"] = "Europe/Warsaw"
            });

            if (ExtraConfiguration is { Count: > 0 })
            {
                configBuilder.AddInMemoryCollection(ExtraConfiguration);
            }
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<schoolmusicContext>>();
            services.RemoveAll<DbContextOptions<schoolmusicContext>>();
            services.RemoveAll<schoolmusicContext>();
            services.AddDbContext<schoolmusicContext>(options =>
                options.UseInMemoryDatabase(DatabaseName));

            services.RemoveAll<IConnectionMultiplexer>();
            services.AddSingleton(Redis.MultiplexerMock.Object);

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(TimeProviderInstance);

            services.RemoveAll<ILdapService>();
            services.AddSingleton(LdapMock.Object);

            services.RemoveAll<ISongSourceService>();
            services.AddSingleton(SongSourceMock.Object);

            services.RemoveAll<IAudioPlayerService>();
            services.AddSingleton(AudioPlayerMock.Object);
        });
    }

    public async Task SeedRanksAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        if (!await db.Ranks.AnyAsync())
        {
            db.Ranks.AddRange(
                new Rank { Id = 1, Rank1 = "user" },
                new Rank { Id = 2, Rank1 = "mod" },
                new Rank { Id = 3, Rank1 = "admin" },
                new Rank { Id = 4, Rank1 = "head_admin" }
            );
            await db.SaveChangesAsync();
        }
    }

    public async Task<User> SeedUserAsync(
        string login,
        string password,
        int rankId = 1,
        string? displayName = null,
        string? email = null,
        bool emailVerified = false,
        string authProvider = "local")
    {
        await SeedRanksAsync();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        var user = new User
        {
            UserLogin = login,
            UserPassword = string.IsNullOrEmpty(password)
                ? BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"))
                : BCrypt.Net.BCrypt.HashPassword(password),
            RankId = rankId,
            DisplayName = displayName,
            Email = email?.Trim().ToLowerInvariant(),
            EmailVerifiedAt = emailVerified ? TimeProviderInstance.GetUtcNow().UtcDateTime : null,
            AuthProvider = authProvider,
            TokenVersion = 1,
            CreatedAt = TimeProviderInstance.GetUtcNow().UtcDateTime
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public ILocalEmailSink EmailSink => Services.GetRequiredService<ILocalEmailSink>();
}
