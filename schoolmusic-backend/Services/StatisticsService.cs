using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;
using StackExchange.Redis;

namespace schoolmusic_backend.Services;

/// <summary>
/// Zestawienie liczby oddanych głosów: ogółem, w bieżącym tygodniu oraz dzisiaj.
/// </summary>
public sealed record VoteTotalsDto(int Total, int ThisWeek, int Today);

/// <summary>
/// Udział gatunku w liczbie odtworzeń z ostatnich 30 dni.
/// </summary>
public sealed record GenreStatDto(string Genre, int PlaysCount, double SharePercent);

/// <summary>
/// Aktywność (głosy i odtworzenia) w jednym dniu z ostatniego tygodnia.
/// </summary>
public sealed record DailyActivityDto(DateOnly Date, int VotesCount, int PlaysCount);

/// <summary>
/// Pozycja w rankingu najczęściej odtwarzanych utworów.
/// </summary>
public sealed record TopSongStatDto(int Position, int SongId, string Title, string ArtistName, int PlaysCount, int TotalVotes);

/// <summary>
/// Pozycja w rankingu najczęściej odtwarzanych wykonawców.
/// </summary>
public sealed record TopArtistStatDto(int Position, int ArtistId, string ArtistName, int PlaysCount, int TotalVotes);

/// <summary>
/// Wyjątek rzucany, gdy statystyki są chwilowo niedostępne (np. awaria bazy danych).
/// Mapowany przez kontroler na kod HTTP 503 Service Unavailable.
/// </summary>
public class StatisticsUnavailableException : Exception
{
    public StatisticsUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Komplet danych dla panelu administratora: liczniki głosów, rozkład gatunków,
/// aktywność dniowa oraz rankingi utworów i wykonawców.
/// </summary>
public sealed record DashboardStatisticsDto(
    DateTime GeneratedAt,
    VoteTotalsDto Votes,
    IReadOnlyList<GenreStatDto> Genres,
    IReadOnlyList<DailyActivityDto> DailyActivity,
    IReadOnlyList<TopSongStatDto> TopSongs,
    IReadOnlyList<TopArtistStatDto> TopArtists,
    bool IsAvailable = true
);

/// <summary>
/// Zbiera statystyki dla panelu administratora. Wynik jest cache'owany w Redis,
/// a w razie niedostępności Redisu zostaje policzony bezpośrednio z bazy.
/// </summary>
public interface IStatisticsService
{
    Task<DashboardStatisticsDto> GetDashboardAsync(CancellationToken cancellationToken = default);
}

public sealed class StatisticsService : IStatisticsService
{
    private const int TopSongsLimit = 10;
    private const int TopArtistsLimit = 10;
    private const int GenresLimit = 10;
    private const int GenreDaysWindow = 30;
    private const int RankingsDaysWindow = 30;
    private const int DailyActivityDays = 7;
    private const string CacheKey = "statistics:dashboard";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly schoolmusicContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StatisticsService> _logger;
    private readonly IStatisticsRefreshCoordinator _refreshCoordinator;
    private static readonly IStatisticsRefreshCoordinator DefaultRefreshCoordinator = new StatisticsRefreshCoordinator();

    public StatisticsService(
        schoolmusicContext context,
        IConnectionMultiplexer redis,
        TimeProvider timeProvider,
        ILogger<StatisticsService> logger,
        IStatisticsRefreshCoordinator? refreshCoordinator = null)
    {
        _context = context;
        _redis = redis;
        _timeProvider = timeProvider;
        _logger = logger;
        _refreshCoordinator = refreshCoordinator ?? DefaultRefreshCoordinator;
    }

    /// <summary>
    /// Zwraca komplet statystyk panelu administratora. Najpierw sprawdza Redis,
    /// a przy braku wpisu liczy dane z bazy i zapisuje wynik w cache na 60 sekund.
    /// </summary>
    /// <param name="cancellationToken">Token anulowania operacji.</param>
    /// <returns>Zestawienie statystyk dla dashboardu.</returns>
    public async Task<DashboardStatisticsDto> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var cached = await TryReadCacheAsync(cancellationToken);
        if (cached != null)
        {
            return cached;
        }

        return await _refreshCoordinator.ExecuteAsync(async () =>
        {
            var refreshedCache = await TryReadCacheAsync(cancellationToken);
            if (refreshedCache != null)
                return refreshedCache;

            try
            {
                var dashboard = await BuildDashboardAsync(cancellationToken);
                await TryWriteCacheAsync(dashboard, cancellationToken);
                return dashboard;
            }
            catch (Exception ex) when (IsDatabaseUnavailable(ex))
            {
                _logger.LogError(ex, "Błąd podczas generowania statystyk z bazy danych.");
                throw new StatisticsUnavailableException(
                    "Statystyki są chwilowo niedostępne z powodu problemu z bazą danych. Spróbuj ponownie za chwilę.",
                    ex);
            }
        }, cancellationToken);
    }

    private async Task<DashboardStatisticsDto?> TryReadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            var db = _redis.GetDatabase();
            var cachedValue = await db.StringGetAsync(CacheKey);
            if (cachedValue.IsNullOrEmpty)
            {
                return null;
            }

            var dashboard = JsonSerializer.Deserialize<DashboardStatisticsDto>(cachedValue.ToString(), JsonOptions);
            if (dashboard == null || dashboard.Votes == null || dashboard.Genres == null ||
                dashboard.DailyActivity == null || dashboard.TopSongs == null || dashboard.TopArtists == null)
            {
                await RemoveInvalidCacheAsync(db);
                return null;
            }

            return dashboard;
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Nie udało się odczytać statystyk z cache Redis — dane zostaną policzone z bazy.");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Cache statystyk Redis zawiera nieprawidłowe dane — wpis zostanie pominięty.");
            await RemoveInvalidCacheAsync(_redis.GetDatabase());
            return null;
        }
    }

    private async Task TryWriteCacheAsync(DashboardStatisticsDto dashboard, CancellationToken cancellationToken)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.StringSetAsync(
                CacheKey,
                JsonSerializer.Serialize(dashboard, JsonOptions),
                (TimeSpan?)CacheTtl,
                When.Always);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Nie udało się zapisać statystyk w cache Redis.");
        }
    }

    private async Task<DashboardStatisticsDto> BuildDashboardAsync(CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var localNow = _timeProvider.GetLocalNow().DateTime;
        var today = localNow.Date;
        var todayStartUtc = ToUtc(today);

        var totalVotes = await _context.Votes.CountAsync(cancellationToken);
        var thisWeekVotes = await _context.Votes.CountAsync(v => v.VotedAt >= utcNow.AddDays(-7), cancellationToken);
        var todayVotes = await _context.Votes.CountAsync(v => v.VotedAt >= todayStartUtc, cancellationToken);

        var genres = await GetGenreStatsAsync(utcNow, cancellationToken);
        var dailyActivity = await GetDailyActivityAsync(today, cancellationToken);
        var topSongs = await GetTopSongsAsync(utcNow, cancellationToken);
        var topArtists = await GetTopArtistsAsync(utcNow, cancellationToken);

        return new DashboardStatisticsDto(
            GeneratedAt: localNow,
            Votes: new VoteTotalsDto(
                Total: totalVotes,
                ThisWeek: thisWeekVotes,
                Today: todayVotes
            ),
            Genres: genres,
            DailyActivity: dailyActivity,
            TopSongs: topSongs,
            TopArtists: topArtists,
            IsAvailable: true
        );
    }

    /// <summary>
    /// Zlicza odtworzenia w ostatnich 30 dniach w rozbiciu na gatunki muzyczne.
    /// </summary>
    private async Task<IReadOnlyList<GenreStatDto>> GetGenreStatsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var windowStart = now.AddDays(-GenreDaysWindow);

        var genrePlays = await _context.Histories
            .AsNoTracking()
            .Where(h => h.PlayedAt >= windowStart)
            .Join(
                _context.Songs.AsNoTracking().Where(s => s.Genre != null && s.Genre != ""),
                h => h.SongId,
                s => s.Id,
                (h, s) => s.Genre!)
            .GroupBy(g => g)
            .Select(g => new { Genre = g.Key, PlaysCount = g.Count() })
            .OrderByDescending(x => x.PlaysCount)
            .ThenBy(x => x.Genre)
            .Take(GenresLimit)
            .ToListAsync(cancellationToken);

        if (genrePlays.Count == 0)
        {
            return Array.Empty<GenreStatDto>();
        }

        var windowPlaysTotal = await _context.Histories
            .AsNoTracking()
            .Where(h => h.PlayedAt >= windowStart)
            .CountAsync(cancellationToken);

        return genrePlays
            .Select(g => new GenreStatDto(
                Genre: g.Genre,
                PlaysCount: g.PlaysCount,
                SharePercent: windowPlaysTotal == 0
                    ? 0d
                    : Math.Round(g.PlaysCount * 100d / windowPlaysTotal, 2)
            ))
            .ToList();
    }

    /// <summary>
    /// Zwraca aktywność z ostatnich 7 dni (liczbę głosów i odtworzeń) w kolejności rosnącej daty.
    /// Dni bez aktywności uzupełniane są zerami, aby wykres miał ciągłą oś czasu.
    /// </summary>
    private async Task<IReadOnlyList<DailyActivityDto>> GetDailyActivityAsync(DateTime today, CancellationToken cancellationToken)
    {
        var windowStartLocal = today.AddDays(-(DailyActivityDays - 1));
        var windowStartUtc = ToUtc(windowStartLocal);

        var voteTimes = await _context.Votes
            .AsNoTracking()
            .Where(v => v.VotedAt >= windowStartUtc)
            .Select(v => v.VotedAt)
            .ToListAsync(cancellationToken);

        var playTimes = await _context.Histories
            .AsNoTracking()
            .Where(h => h.PlayedAt >= windowStartUtc)
            .Select(h => h.PlayedAt)
            .ToListAsync(cancellationToken);

        var votesLookup = voteTimes
            .GroupBy(ToSchoolDate)
            .ToDictionary(group => group.Key, group => group.Count());
        var playsLookup = playTimes
            .GroupBy(ToSchoolDate)
            .ToDictionary(group => group.Key, group => group.Count());

        var activity = new List<DailyActivityDto>(DailyActivityDays);
        for (int offset = DailyActivityDays - 1; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            votesLookup.TryGetValue(date, out int votesCount);
            playsLookup.TryGetValue(date, out int playsCount);
            activity.Add(new DailyActivityDto(Date: DateOnly.FromDateTime(date), VotesCount: votesCount, PlaysCount: playsCount));
        }

        return activity;
    }

    private async Task RemoveInvalidCacheAsync(IDatabase db)
    {
        try
        {
            await db.KeyDeleteAsync(CacheKey);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Nie udało się usunąć nieprawidłowego cache statystyk z Redis.");
        }
    }

    private static bool IsDatabaseUnavailable(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is DbException or TimeoutException)
                return true;
        }

        return false;
    }

    private DateTime ToUtc(DateTime schoolLocalTime) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(schoolLocalTime, DateTimeKind.Unspecified),
            _timeProvider.LocalTimeZone);

    private DateTime ToSchoolDate(DateTime utcTime) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utcTime, DateTimeKind.Utc),
            _timeProvider.LocalTimeZone).Date;

    /// <summary>
    /// Zwraca 10 najczęściej odtwarzanych utworów z ostatnich 30 dni.
    /// </summary>
    private async Task<IReadOnlyList<TopSongStatDto>> GetTopSongsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var windowStart = now.AddDays(-RankingsDaysWindow);

        var topSongStats = await _context.Histories
            .AsNoTracking()
            .Where(h => h.PlayedAt >= windowStart)
            .GroupBy(h => h.SongId)
            .Select(g => new
            {
                SongId = g.Key,
                PlaysCount = g.Count(),
                TotalVotes = g.Sum(h => h.Votes)
            })
            .OrderByDescending(x => x.PlaysCount)
            .ThenByDescending(x => x.TotalVotes)
            .Take(TopSongsLimit)
            .ToListAsync(cancellationToken);

        if (topSongStats.Count == 0)
        {
            return Array.Empty<TopSongStatDto>();
        }

        var songIds = topSongStats.Select(x => x.SongId).ToList();
        var songsDict = await _context.Songs
            .AsNoTracking()
            .Where(s => songIds.Contains(s.Id))
            .Include(s => s.Artist)
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        return topSongStats
            .Where(x => songsDict.ContainsKey(x.SongId))
            .Select((x, index) =>
            {
                var song = songsDict[x.SongId];
                return new TopSongStatDto(
                    Position: index + 1,
                    SongId: song.Id,
                    Title: song.Title,
                    ArtistName: song.Artist?.Name ?? "Nieznany wykonawca",
                    PlaysCount: x.PlaysCount,
                    TotalVotes: x.TotalVotes
                );
            })
            .ToList();
    }

    /// <summary>
    /// Zwraca 10 najczęściej odtwarzanych wykonawców z ostatnich 30 dni (dla wykonawcy sumuje wszystkie jego utwory).
    /// </summary>
    private async Task<IReadOnlyList<TopArtistStatDto>> GetTopArtistsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var windowStart = now.AddDays(-RankingsDaysWindow);

        var topArtistStats = await _context.Histories
            .AsNoTracking()
            .Where(h => h.PlayedAt >= windowStart)
            .Join(
                _context.Songs.AsNoTracking(),
                h => h.SongId,
                s => s.Id,
                (h, s) => new { h.Votes, s.ArtistId })
            .GroupBy(x => x.ArtistId)
            .Select(g => new
            {
                ArtistId = g.Key,
                PlaysCount = g.Count(),
                TotalVotes = g.Sum(x => x.Votes)
            })
            .OrderByDescending(x => x.PlaysCount)
            .ThenByDescending(x => x.TotalVotes)
            .Take(TopArtistsLimit)
            .ToListAsync(cancellationToken);

        if (topArtistStats.Count == 0)
        {
            return Array.Empty<TopArtistStatDto>();
        }

        var artistIds = topArtistStats.Select(x => x.ArtistId).ToList();
        var artistsDict = await _context.Artists
            .AsNoTracking()
            .Where(a => artistIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        var result = topArtistStats
            .Where(x => artistsDict.ContainsKey(x.ArtistId))
            .Select((x, index) => new TopArtistStatDto(
                Position: index + 1,
                ArtistId: x.ArtistId,
                ArtistName: artistsDict[x.ArtistId].Name ?? "Nieznany wykonawca",
                PlaysCount: x.PlaysCount,
                TotalVotes: x.TotalVotes
            ))
            .ToList();

        return result;
    }
}
