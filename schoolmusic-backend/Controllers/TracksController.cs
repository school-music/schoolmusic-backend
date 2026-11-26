using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Models;
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

public sealed record ExternalTrackSearchItemDto(
    string Title,
    string Artist,
    string ArtistName,
    string? Cover,
    int DurationMs,
    string ExternalId,
    string? SpotifyId,
    string Source
);

public sealed record FavouriteTrackItemDto(
    int Id,
    int SongId,
    string? SpotifyId,
    string Title,
    int ArtistId,
    string ArtistName,
    string? Cover,
    int DurationMs,
    string? Genre,
    string Source,
    string? SongUrl
);

public sealed record FavouriteTracksResponseDto(
    string Message,
    IReadOnlyList<FavouriteTrackItemDto> Tracks
);

public sealed record FavouriteMutationResponseDto(
    string Message,
    int SongId
);

/// <summary>
/// Kontroler API obsługujący operacje związane z utworami: przeglądanie katalogu, wyszukiwanie zewnętrzne i zarządzanie ulubionymi.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
[Produces("application/json")]
public class TracksController : ControllerBase
{
    private readonly schoolmusicContext _context;
    private readonly IBreakService _breakService;
    private readonly IAudioPlayerService _audioPlayerService;
    private readonly ISongSourceService _songSourceService;
    private readonly TimeProvider _timeProvider;
    private readonly IVotingTargetService _votingTargetService;

    public TracksController(
        schoolmusicContext context,
        IBreakService breakService,
        IAudioPlayerService audioPlayerService,
        ISongSourceService songSourceService,
        TimeProvider? timeProvider = null,
        IVotingTargetService? votingTargetService = null)
    {
        _context = context;
        _breakService = breakService;
        _audioPlayerService = audioPlayerService;
        _songSourceService = songSourceService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _votingTargetService = votingTargetService ?? new VotingTargetService(context, timeProvider: _timeProvider);
    }

    /// <summary>
    /// Wyszukuje utwory w zewnętrznym katalogu (np. Spotify API).
    /// Parametr wyszukiwania może być przekazany w nagłówku HTTP 'q', w query stringu '?q=...' lub '?query=...'.
    /// </summary>
    [HttpGet("search")]
    [AllowAnonymous]
    [ProducesResponseType<IReadOnlyList<ExternalTrackSearchItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SearchTracks(
        [FromQuery(Name = "q")] string? q = null,
        [FromHeader(Name = "q")] string? headerQ = null,
        [FromQuery(Name = "query")] string? query = null)
    {
        var searchQuery = !string.IsNullOrWhiteSpace(headerQ) ? headerQ
            : (!string.IsNullOrWhiteSpace(q) ? q : query);

        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return this.Create(StatusCodes.Status400BadRequest, "Parametr q jest wymagany.");
        }

        if (!_songSourceService.IsConfigured)
        {
            return this.Create(
                StatusCodes.Status503ServiceUnavailable,
                "Wyszukiwanie Spotify jest niedostępne. Skonfiguruj Spotify:ClientId i Spotify:ClientSecret.");
        }

        var tracks = await _songSourceService.SearchTracksAsync(searchQuery.Trim());
        var items = tracks.Select(track => new ExternalTrackSearchItemDto(
            Title: track.Title,
            Artist: track.ArtistName,
            ArtistName: track.ArtistName,
            Cover: track.CoverUrl,
            DurationMs: track.DurationMs,
            ExternalId: track.ExternalId,
            SpotifyId: string.Equals(track.Source, "spotify", StringComparison.OrdinalIgnoreCase)
                ? track.ExternalId
                : null,
            Source: track.Source
        )).ToList();

        return Ok(items);
    }

    /// <summary>
    /// Zwraca listę utworów z bazy z możliwością wyszukiwania, filtrowania po gatunku, sortowania i paginacji.
    /// Zawiera informację o liczbie głosów na bieżącą/wskazaną przerwę oraz flagę polubienia dla zalogowanego użytkownika.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<TracksPagedResponseDto>(StatusCodes.Status200OK)]
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
    [ProducesResponseType<GenresListResponseDto>(StatusCodes.Status200OK)]
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
    [ProducesResponseType<TrackListItemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTrackById(int id, [FromQuery] int? breakId = null)
    {
        var song = await _context.Songs
            .Include(s => s.Artist)
            .FirstOrDefaultAsync(s => s.Id == id && s.IsActive);

        if (song == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono utworu o podanym ID.");
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

        var target = await _votingTargetService.GetVotingTargetAsync();
        if (target.EligibleBreakId.HasValue)
        {
            return target.EligibleBreakId.Value;
        }

        if (target.BreakId.HasValue)
        {
            return target.BreakId.Value;
        }

        return await _context.Breaks.OrderBy(b => b.BreakNumber).Select(b => (int?)b.Id).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Zwraca listę ulubionych utworów zalogowanego użytkownika wraz z wykonawcą i metadanymi źródła.
    /// </summary>
    [HttpGet("favourites")]
    [ProducesResponseType<FavouriteTracksResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAllFavourites()
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return this.Create(StatusCodes.Status401Unauthorized, "Token JWT niepoprawny");
        }

        var songs = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == currentUserId.Value)
            .SelectMany(u => u.Songs)
            .Include(s => s.Artist)
            .Select(s => new FavouriteTrackItemDto(
                s.Id,
                s.Id,
                s.SpotifyId,
                s.Title,
                s.ArtistId,
                s.Artist != null ? s.Artist.Name : "Nieznany wykonawca",
                s.Cover,
                s.DurationMs,
                s.Genre,
                s.Source,
                s.SongUrl
            ))
            .ToListAsync();

        return Ok(new FavouriteTracksResponseDto(
            Message: "Ulubione utwory użytkownika",
            Tracks: songs
        ));
    }

    /// <summary>
    /// Dodaje utwór po id w bazie danych do ulubionych zalogowanego użytkownika
    /// </summary>
    [HttpPost("{id:int}/favourites")]
    [ProducesResponseType<FavouriteMutationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddToFavourites(int id)
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return this.Create(StatusCodes.Status401Unauthorized, "Token JWT niepoprawny");
        }

        var user = await _context.Users.Include(u => u.Songs)
            .FirstOrDefaultAsync(u => u.Id == currentUserId);
        if (user == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Użytkownik nie znaleziony");
        }

        var song = await _context.Songs.FindAsync(id);
        if (song == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono piosenki.");
        }

        if (!user.Songs.Contains(song))
        {
            user.Songs.Add(song);
            await _context.SaveChangesAsync();
            return Ok(new FavouriteMutationResponseDto("Poprawnie dodano do ulubionych", song.Id));
        }

        return this.Create(StatusCodes.Status409Conflict, "Piosenka jest już w ulubionych");
    }

    /// <summary>
    /// Dodaje utwór o podanym identyfikatorze Spotify do listy ulubionych bieżącego użytkownika
    /// </summary>
    [HttpPost("spotify/{spotifyId}/favourites")]
    [ProducesResponseType<FavouriteMutationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AddToFavouritesSpotifyId(string spotifyId)
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return this.Create(StatusCodes.Status401Unauthorized, "Token JWT niepoprawny");
        }

        var user = await _context.Users.Include(u => u.Songs)
            .FirstOrDefaultAsync(u => u.Id == currentUserId);

        if (user == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono użytkownika o podanym ID");
        }

        var normalizedId = SpotifyTrackImporter.NormalizeSpotifyTrackId(spotifyId);
        var song = await _context.Songs
            .FirstOrDefaultAsync(candidate => candidate.SpotifyId == normalizedId || candidate.SpotifyId == spotifyId);
        if (song == null)
        {
            if (!_songSourceService.IsConfigured)
            {
                return this.Create(
                    StatusCodes.Status503ServiceUnavailable,
                    "Dodawanie utworów Spotify jest niedostępne. Skonfiguruj Spotify:ClientId i Spotify:ClientSecret.");
            }

            song = await SpotifyTrackImporter.GetOrImportAsync(_context, _songSourceService, spotifyId);
            if (song == null)
            {
                return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono utworu o podanym Spotify ID.");
            }
        }

        if (!user.Songs.Contains(song))
        {
            user.Songs.Add(song);
            await _context.SaveChangesAsync();
            return Ok(new FavouriteMutationResponseDto("Poprawnie dodano do ulubionych", song.Id));
        }

        return this.Create(StatusCodes.Status409Conflict, "Piosenka jest już w ulubionych");
    }

    /// <summary>
    /// Usuwa piosenkę z listy ulubionych zalogowanego użytkownika
    /// </summary>
    [HttpDelete("favourites/{id}")]
    [ProducesResponseType<FavouriteMutationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteFromFavourites(int id)
    {
        var currentUserId = HttpContext.GetCurrentUserId();
        if (currentUserId == null)
        {
            return this.Create(StatusCodes.Status401Unauthorized, "Token JWT niepoprawny");
        }

        var user = await _context.Users.Include(u => u.Songs)
            .FirstOrDefaultAsync(u => u.Id == currentUserId);
        var song = await _context.Songs.FirstOrDefaultAsync(s => s.Id == id);
        if (user == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono użytkownika");
        }
        if (song == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono piosenki");
        }

        if (!user.Songs.Remove(song))
        {
            return this.Create(StatusCodes.Status404NotFound, "Piosenka nie znajdowała sie w ulubionych");
        }
        await _context.SaveChangesAsync();
        return Ok(new FavouriteMutationResponseDto("Pomyślnie usunięto piosenke z ulubionych", song.Id));
    }
}
