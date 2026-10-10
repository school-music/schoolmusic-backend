using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services
{
    public record TopSongRankingDto(
        int Position,
        int SongId,
        string Title,
        string ArtistName,
        string Cover,
        int DurationMs,
        int PlaysCount,
        int TotalVotes
    );

    public record TopArtistRankingDto(
        int Position,
        int ArtistId,
        string ArtistName,
        int PlaysCount,
        int TotalVotes
    );

    public record PlaybackHistoryDto(
        int HistoryId,
        int SongId,
        string Title,
        string ArtistName,
        string Cover,
        DateTime PlayedAt,
        int BreakId,
        int? BreakNumber,
        int VotesCount
    );

    public interface IRankingsService
    {
        Task<List<TopSongRankingDto>> GetTopSongsAsync(string? period = "week", int limit = 10);
        Task<List<TopArtistRankingDto>> GetTopArtistsAsync(string? period = "week", int limit = 10);
        Task<List<PlaybackHistoryDto>> GetRecentHistoryAsync(int limit = 20, int offset = 0);
    }

    public class RankingsService : IRankingsService
    {
        private readonly schoolmusicContext _context;

        public RankingsService(schoolmusicContext context)
        {
            _context = context;
        }

        private static DateTime? GetStartDateForPeriod(string? period)
        {
            return period?.Trim().ToLowerInvariant() switch
            {
                "today" or "day" => DateTime.UtcNow.Date,
                "week" => DateTime.UtcNow.AddDays(-7),
                "month" => DateTime.UtcNow.AddDays(-30),
                "year" => DateTime.UtcNow.AddDays(-365),
                "all-time" or "all" => null,
                _ => DateTime.UtcNow.AddDays(-7) // domyślnie ostatnie 7 dni
            };
        }

        public async Task<List<TopSongRankingDto>> GetTopSongsAsync(string? period = "week", int limit = 10)
        {
            var startDate = GetStartDateForPeriod(period);
            var query = _context.Histories.AsQueryable();

            if (startDate.HasValue)
            {
                query = query.Where(h => h.PlayedAt >= startDate.Value);
            }

            var topSongStats = await query
                .GroupBy(h => h.SongId)
                .Select(g => new
                {
                    SongId = g.Key,
                    PlaysCount = g.Count(),
                    TotalVotes = g.Sum(h => h.Votes)
                })
                .OrderByDescending(x => x.PlaysCount)
                .ThenByDescending(x => x.TotalVotes)
                .Take(Math.Clamp(limit, 1, 100))
                .ToListAsync();

            if (topSongStats.Count == 0)
            {
                return new List<TopSongRankingDto>();
            }

            var songIds = topSongStats.Select(x => x.SongId).ToList();
            var songsDict = await _context.Songs
                .Where(s => songIds.Contains(s.Id))
                .Include(s => s.Artist)
                .ToDictionaryAsync(s => s.Id);

            var result = topSongStats
                .Where(x => songsDict.ContainsKey(x.SongId))
                .Select((x, index) =>
                {
                    var song = songsDict[x.SongId];
                    return new TopSongRankingDto(
                        Position: index + 1,
                        SongId: song.Id,
                        Title: song.Title,
                        ArtistName: song.Artist?.Name ?? "Nieznany wykonawca",
                        Cover: song.Cover,
                        DurationMs: song.DurationMs,
                        PlaysCount: x.PlaysCount,
                        TotalVotes: x.TotalVotes
                    );
                })
                .ToList();

            return result;
        }

        public async Task<List<TopArtistRankingDto>> GetTopArtistsAsync(string? period = "week", int limit = 10)
        {
            var startDate = GetStartDateForPeriod(period);
            var query = _context.Histories
                .Include(h => h.Song)
                .AsQueryable();

            if (startDate.HasValue)
            {
                query = query.Where(h => h.PlayedAt >= startDate.Value);
            }

            var topArtistStats = await query
                .GroupBy(h => h.Song.ArtistId)
                .Select(g => new
                {
                    ArtistId = g.Key,
                    PlaysCount = g.Count(),
                    TotalVotes = g.Sum(h => h.Votes)
                })
                .OrderByDescending(x => x.PlaysCount)
                .ThenByDescending(x => x.TotalVotes)
                .Take(Math.Clamp(limit, 1, 100))
                .ToListAsync();

            if (topArtistStats.Count == 0)
            {
                return new List<TopArtistRankingDto>();
            }

            var artistIds = topArtistStats.Select(x => x.ArtistId).ToList();
            var artistsDict = await _context.Artists
                .Where(a => artistIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id);

            var result = topArtistStats
                .Where(x => artistsDict.ContainsKey(x.ArtistId))
                .Select((x, index) =>
                {
                    var artist = artistsDict[x.ArtistId];
                    return new TopArtistRankingDto(
                        Position: index + 1,
                        ArtistId: artist.Id,
                        ArtistName: artist.Name ?? "Nieznany wykonawca",
                        PlaysCount: x.PlaysCount,
                        TotalVotes: x.TotalVotes
                    );
                })
                .ToList();

            return result;
        }

        public async Task<List<PlaybackHistoryDto>> GetRecentHistoryAsync(int limit = 20, int offset = 0)
        {
            var historyItems = await _context.Histories
                .Include(h => h.Song)
                    .ThenInclude(s => s.Artist)
                .Include(h => h.Break)
                .OrderByDescending(h => h.PlayedAt)
                .Skip(Math.Max(0, offset))
                .Take(Math.Clamp(limit, 1, 100))
                .Select(h => new PlaybackHistoryDto(
                    h.Id,
                    h.SongId,
                    h.Song.Title,
                    h.Song.Artist.Name ?? "Nieznany wykonawca",
                    h.Song.Cover,
                    h.PlayedAt,
                    h.BreakId,
                    h.Break.BreakNumber,
                    h.Votes
                ))
                .ToListAsync();

            return historyItems;
        }
    }
}
