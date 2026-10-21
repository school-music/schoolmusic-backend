using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LibVLCSharp.Shared;
using schoolmusic_backend.Models;
using schoolmusic_backend.Extensions;
using static System.Net.WebRequestMethods;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers;

/// <summary>
/// Element listy utworów zwracany w katalogu dla uczniów i panelu WWW
/// </summary>
public record TrackListItemDto(
    int Id,
    string Title,
    int ArtistId,
    string ArtistName,
    string? Cover,
    int DurationMs,
    string? Genre,
    string Source,
    string? SongUrl,
    string? SpotifyId,
    int VotesCount,
    bool IsFavorite,
    int? QueueItemId,
    string? QueueStatus
);

/// <summary>
/// Odpowiedź paginowana dla katalogu utworów
/// </summary>
public record TracksPagedResponseDto(
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    int? TargetBreakId,
    IReadOnlyList<TrackListItemDto> Items
);

/// <summary>
/// Odpowiedź zawierająca listę unikalnych gatunków muzycznych
/// </summary>
public record GenresListResponseDto(
    int Total,
    IReadOnlyList<string> Genres
);

/// <summary>
/// Kontroler API obsługujący operacje związane z utworami: pobieranie i zarządzanie ulubionymi, odtwarzanie plików/URL
/// oraz obliczanie planu przerwy dla kolejki utworów
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class TracksController : ControllerBase
{
    private readonly schoolmusicContext _context;
    private readonly IBreakService _breakService;
    private readonly IAudioPlayerService _audioPlayerService;
    private readonly ISongSourceService _songSourceService;

    public TracksController(
        schoolmusicContext context,
        IBreakService breakService,
        IAudioPlayerService audioPlayerService,
        ISongSourceService songSourceService)
    {
        _context = context;
        _breakService = breakService;
        _audioPlayerService = audioPlayerService;
        _songSourceService = songSourceService;
    }

    [HttpGet("search")]
    [AllowAnonymous]
    public async Task<IActionResult> SearchTracks([FromQuery] string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest(new { Message = "Parametr q jest wymagany." });
        }

        if (!_songSourceService.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                Message = "Wyszukiwanie Spotify jest niedostępne. Skonfiguruj Spotify:ClientId i Spotify:ClientSecret."
            });
        }

        var tracks = await _songSourceService.SearchTracksAsync(q.Trim());
        return Ok(tracks.Select(track => new
        {
            title = track.Title,
            artist = track.ArtistName,
            cover = track.CoverUrl,
            durationMs = track.DurationMs,
            externalId = track.ExternalId
        }));
    }

    /// <summary>
    /// Zwraca listę utworów z bazy z możliwością wyszukiwania, filtrowania po gatunku, sortowania i paginacji.
    /// Zawiera informację o liczbie głosów na bieżącą/wskazaną przerwę oraz flagę polubienia dla zalogowanego użytkownika.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetTracks(
        [FromQuery] string? search = null,
        [FromQuery] string? genre = null,
        [FromQuery] int? breakId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sortBy = "id",
        [FromQuery] string? sortOrder = "asc")
    {
        var query = _context.Songs
            .AsNoTracking()
            .Include(s => s.Artist)
            .Where(s => s.IsActive);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(s => s.Title.ToLower().Contains(term) || (s.Artist != null && s.Artist.Name.ToLower().Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(genre))
        {
            var g = genre.Trim().ToLower();
            query = query.Where(s => s.Genre != null && s.Genre.ToLower() == g);
        }

        int? targetBreakId = await GetEffectiveTargetBreakIdAsync(breakId);

        var order = sortOrder?.ToLowerInvariant() == "desc" ? "desc" : "asc";
        query = (sortBy?.ToLowerInvariant(), order) switch
        {
            ("id", "desc") => query.OrderByDescending(s => s.Id),
            ("id", "asc") => query.OrderBy(s => s.Id),
            ("artist", "desc") => query.OrderByDescending(s => s.Artist.Name).ThenBy(s => s.Title),
            ("artist", "asc") => query.OrderBy(s => s.Artist.Name).ThenBy(s => s.Title),
            ("duration", "desc") => query.OrderByDescending(s => s.DurationMs).ThenBy(s => s.Title),
            ("duration", "asc") => query.OrderBy(s => s.DurationMs).ThenBy(s => s.Title),
            ("newest", "asc") => query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Title),
            ("newest", "desc") => query.OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Title),
            ("votes", "asc") => targetBreakId.HasValue
                ? query.OrderBy(s => _context.QueueItems
                    .Where(q => q.SongId == s.Id && q.BreakId == targetBreakId.Value && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                    .SelectMany(q => q.Votes).Count()).ThenBy(s => s.Title)
                : query.OrderBy(s => s.Title),
            ("votes", "desc") => targetBreakId.HasValue
                ? query.OrderByDescending(s => _context.QueueItems
                    .Where(q => q.SongId == s.Id && q.BreakId == targetBreakId.Value && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                    .SelectMany(q => q.Votes).Count()).ThenBy(s => s.Title)
                : query.OrderBy(s => s.Title),
            ("title", "desc") => query.OrderByDescending(s => s.Title),
            ("title", "asc") => query.OrderBy(s => s.Title),
            _ => query.OrderBy(s => s.Id)
        };

        int total = await query.CountAsync();
        int safePage = Math.Max(1, page);
        int safePageSize = Math.Clamp(pageSize, 1, 100);
        int totalPages = (int)Math.Ceiling(total / (double)safePageSize);

        var pageSongs = await query
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync();

        var songIds = pageSongs.Select(s => s.Id).ToList();

        // Pobranie danych kolejki i głosów dla wyświetlanej strony
        var queueLookup = new Dictionary<int, (int votesCount, int queueItemId, string status)>();
        if (targetBreakId.HasValue && songIds.Count > 0)
        {
            var queueItems = await _context.QueueItems
                .Where(q => q.BreakId == targetBreakId.Value && songIds.Contains(q.SongId) && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                .Select(q => new
                {
                    q.SongId,
                    q.Id,
                    q.Status,
                    VotesCount = q.Votes.Count
                })
                .ToListAsync();

            foreach (var qi in queueItems)
            {
                queueLookup[qi.SongId] = (qi.VotesCount, qi.Id, qi.Status);
            }
        }

        // Sprawdzenie ulubionych dla zalogowanego użytkownika
        var currentUserId = HttpContext.GetCurrentUserId();
        var favoriteSongIds = new HashSet<int>();
        if (currentUserId.HasValue && songIds.Count > 0)
        {
            var favs = await _context.Users
                .Where(u => u.Id == currentUserId.Value)
                .SelectMany(u => u.Songs.Where(s => songIds.Contains(s.Id)).Select(s => s.Id))
                .ToListAsync();
            favoriteSongIds = favs.ToHashSet();
        }

        var items = pageSongs.Select(s =>
        {
            bool inQueue = queueLookup.TryGetValue(s.Id, out var qInfo);
            return new TrackListItemDto(
                Id: s.Id,
                Title: s.Title,
                ArtistId: s.ArtistId,
                ArtistName: s.Artist?.Name ?? "Nieznany wykonawca",
                Cover: s.Cover,
                DurationMs: s.DurationMs,
                Genre: s.Genre,
                Source: s.Source,
                SongUrl: s.SongUrl,
                SpotifyId: s.SpotifyId,
                VotesCount: inQueue ? qInfo.votesCount : 0,
                IsFavorite: favoriteSongIds.Contains(s.Id),
                QueueItemId: inQueue ? qInfo.queueItemId : null,
                QueueStatus: inQueue ? qInfo.status : null
            );
        }).ToList();

        var response = new TracksPagedResponseDto(
            Total: total,
            Page: safePage,
            PageSize: safePageSize,
            TotalPages: totalPages,
            TargetBreakId: targetBreakId,
            Items: items
        );

        return Ok(response);
    }

    /// <summary>
    /// Zwraca listę unikalnych gatunków muzycznych obecnych w bazie aktywnych utworów
    /// </summary>
    [HttpGet("genres")]
    [AllowAnonymous]
    public async Task<IActionResult> GetGenres()
    {
        var genres = await _context.Songs
            .Where(s => s.IsActive && !string.IsNullOrWhiteSpace(s.Genre))
            .Select(s => s.Genre!)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync();

        return Ok(new GenresListResponseDto(
            Total: genres.Count,
            Genres: genres
        ));
    }

    /// <summary>
    /// Zwraca szczegóły pojedynczego utworu wraz z informacją o głosach na bieżącą/wskazaną przerwę
    /// </summary>
    [HttpGet("{id:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetTrackById(int id, [FromQuery] int? breakId = null)
    {
        var song = await _context.Songs
            .Include(s => s.Artist)
            .FirstOrDefaultAsync(s => s.Id == id && s.IsActive);

        if (song == null)
        {
            return NotFound(new { Message = "Nie znaleziono utworu o podanym ID." });
        }

        int? targetBreakId = await GetEffectiveTargetBreakIdAsync(breakId);
        int votesCount = 0;
        int? queueItemId = null;
        string? queueStatus = null;

        if (targetBreakId.HasValue)
        {
            var qItem = await _context.QueueItems
                .Where(q => q.BreakId == targetBreakId.Value && q.SongId == song.Id && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
                .Select(q => new
                {
                    q.Id,
                    q.Status,
                    VotesCount = q.Votes.Count
                })
                .FirstOrDefaultAsync();

            if (qItem != null)
            {
                votesCount = qItem.VotesCount;
                queueItemId = qItem.Id;
                queueStatus = qItem.Status;
            }
        }

        var currentUserId = HttpContext.GetCurrentUserId();
        bool isFavorite = false;
        if (currentUserId.HasValue)
        {
            isFavorite = await _context.Users
                .Where(u => u.Id == currentUserId.Value)
                .AnyAsync(u => u.Songs.Any(s => s.Id == song.Id));
        }

        var dto = new TrackListItemDto(
            Id: song.Id,
            Title: song.Title,
            ArtistId: song.ArtistId,
            ArtistName: song.Artist?.Name ?? "Nieznany wykonawca",
            Cover: song.Cover,
            DurationMs: song.DurationMs,
            Genre: song.Genre,
            Source: song.Source,
            SongUrl: song.SongUrl,
            SpotifyId: song.SpotifyId,
            VotesCount: votesCount,
            IsFavorite: isFavorite,
            QueueItemId: queueItemId,
            QueueStatus: queueStatus
        );

        return Ok(dto);
    }

    private async Task<int?> GetEffectiveTargetBreakIdAsync(int? requestedBreakId)
    {
        if (requestedBreakId.HasValue)
        {
            var exists = await _context.Breaks.AnyAsync(b => b.Id == requestedBreakId.Value);
            return exists ? requestedBreakId.Value : null;
        }

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var allBreaks = await _context.Breaks.OrderBy(b => b.BreakNumber).ToListAsync();
        if (allBreaks.Count == 0) return null;

        var activeBreak = allBreaks.FirstOrDefault(b =>
        {
            var start = today.ToDateTime(TimeOnly.FromDateTime(b.StartAt));
            var end = today.ToDateTime(TimeOnly.FromDateTime(b.EndsAt));
            return now >= start && now < end;
        });

        if (activeBreak != null)
        {
            return activeBreak.Id;
        }

        var upcoming = allBreaks.FirstOrDefault(b =>
        {
            var voteEnd = b.VoteEnds.HasValue
                ? today.ToDateTime(TimeOnly.FromDateTime(b.VoteEnds.Value))
                : today.ToDateTime(TimeOnly.FromDateTime(b.StartAt));
            return voteEnd > now;
        });

        return upcoming?.Id ?? allBreaks.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// Zwraca listę ulubionych utworów zalogowanego użytkownika
    /// </summary>
    /// <returns>Task<IActionResult> — 200 OK zawierające obiekt z polem Message i kolekcją Tracks (Id, SpotifyId, Title, Cover,
    /// DurationMs); 401 Unauthorized gdy token JWT jest nieprawidłowy</returns>
    [HttpGet("favourites")]
    public async Task<IActionResult> GetAllFavourites()
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return Unauthorized(new
            {
                Message = "Token JWT niepoprawny"
            });
        }

        //var songs = user.Songs; błąd serializacji przez tabele w MysQL
        var songs = await _context.Users.Where(u => u.Id == currentUserId)
            .SelectMany(u => u.Songs)
            .Select(s => new
            {
                s.Id,
                s.SpotifyId,
                s.Title,
                s.Cover,
                s.DurationMs,
            }).ToListAsync();
        return Ok(new
        {
            Message = "Ulubione utwory użytkownika",
            Tracks = songs
        });

    }


    /// <summary>
    /// Dodawanie po id w bazie danych
    /// </summary>
    [HttpPost("{id:int}/favourites")]
    public async Task<IActionResult> AddToFavourites(int id)
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return Unauthorized(new
            {
                Message = "Token JWT niepoprawny"
            });
        }

        var user = await _context.Users.Include(u => u.Songs)
            .FirstOrDefaultAsync(u => u.Id == currentUserId);
        if (user == null)
        {
            return NotFound(new { Message = "Użytkownik nie znaleziony" });
        }

        var song = await _context.Songs.FindAsync(id);
        if (song == null)
        {
            return NotFound(new { Message = "Nie znaleziono piosenki." });
        }


        if (!user.Songs.Contains(song))
        {
            user.Songs.Add(song);
            await _context.SaveChangesAsync();
            return Ok(new
            {
                Message = "Poprawnie dodano do ulubionych"
            });
        }
        else
        {
            return Conflict(new
            {
                Message = "Piosenka jest już w ulubionych"
            });
        }

    }

    /// <summary>
    /// Dodaje utwór o podanym identyfikatorze Spotify do listy ulubionych bieżącego użytkownika
    /// </summary>
    /// <returns>200 OK po pomyślnym dodaniu; 401 Unauthorized przy brakującym lub niepoprawnym tokenie; 404 NotFound gdy nie
    /// znaleziono użytkownika lub utworu; 409 Conflict gdy utwór już znajduje się w ulubionych</returns>
    [HttpPost("spotify/{spotifyId}/favourites")]
    public async Task<IActionResult> AddToFavouritesSpotifyId(string spotifyId)
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return Unauthorized(new
            {
                Message = "Token JWT niepoprawny"
            });
        }
        var user = await _context.Users.Include(u => u.Songs)
            .FirstOrDefaultAsync(u => u.Id == currentUserId);

        if (user == null)
        {
            return NotFound(new
            {
                Message = "Nie znaleziono użytkownika o podanym ID"
            });
        }

        var song = await _context.Songs
            .FirstOrDefaultAsync(candidate => candidate.SpotifyId == spotifyId);
        if (song == null)
        {
            if (!_songSourceService.IsConfigured)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    Message = "Dodawanie utworów Spotify jest niedostępne. Skonfiguruj Spotify:ClientId i Spotify:ClientSecret."
                });
            }

            song = await SpotifyTrackImporter.GetOrImportAsync(_context, _songSourceService, spotifyId);
            if (song == null)
            {
                return NotFound(new { Message = "Nie znaleziono utworu o podanym Spotify ID." });
            }
        }
        if (!user.Songs.Contains(song))
        {
            user.Songs.Add(song);
            await _context.SaveChangesAsync();
            return Ok(new
            {
                Message = "Poprawnie dodano do ulubionych"
            });
        }
        else
        {
            return Conflict(new
            {
                Message = "Piosenka już jest w ulubionych"
            });
        }
    }

    /// <summary>
    /// Usuwa piosenkę z listy ulubionych zalogowanego użytkownika
    /// </summary>
    /// <returns>Zwraca IActionResult: 200 (OK) po pomyślnym usunięciu; 401 (Unauthorized) jeśli brak lub niepoprawny token; 404
    /// (NotFound) jeśli użytkownik, piosenka lub powiązanie piosenki z ulubionymi nie istnieje</returns>
    [HttpDelete("favourites/{id}")]
    public async Task<IActionResult> DeleteFromFavourites(int id)
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return Unauthorized(new
            {
                Message = "Token JWT niepoprawny"
            });
        }

        var user = await _context.Users.Include(u => u.Songs)
            .FirstOrDefaultAsync(u => u.Id == currentUserId);
        var song = await _context.Songs.FirstOrDefaultAsync(s => s.Id == id);
        if (user == null)
        {
            return NotFound(new
            {
                Message = "Nie znaleziono użytkownika"
            });
        }
        if (song == null)
        {
            return NotFound(new
            {
                Message = "Nie znaleziono piosenki"
            });
        }

        if (!user.Songs.Remove(song))
        {
            return NotFound(new
            {
                Message = "Piosenka nie znajdowała sie w ulubionych"
            });
        }
        await _context.SaveChangesAsync();
        return Ok(new
        {
            Message = "Pomyślnie usunięto piosenke z ulubionych"
        });

    }
}