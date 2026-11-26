using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers;

/// <summary>
/// Model żądania dodania nowego utworu przez administratora
/// </summary>
public record CreateTrackDto(
    string Title,
    string ArtistName,
    string? Genre,
    int DurationMs,
    string? SongUrl,
    string? Cover,
    string? Source = "other",
    string? SpotifyId = null
);

/// <summary>
/// Model żądania aktualizacji istniejącego utworu przez administratora
/// </summary>
public record UpdateTrackDto(
    string? Title,
    string? ArtistName,
    string? Genre,
    int? DurationMs,
    string? SongUrl,
    string? Cover,
    string? Source,
    string? SpotifyId,
    bool? IsActive
);

/// <summary>
/// Model reprezentujący utwór w panelu administracyjnym z pełnymi metadanymi i statystykami
/// </summary>
public record AdminTrackItemDto(
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
    bool IsActive,
    DateTime CreatedAt,
    int TotalPlaysCount,
    int CurrentQueueVotesCount
);

/// <summary>
/// Odpowiedź stronicowana dla listy utworów w panelu administratora
/// </summary>
public record AdminTracksPagedResponseDto(
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    IReadOnlyList<AdminTrackItemDto> Items
);

public sealed record AdminTrackStatusResponseDto(
    string Message,
    int Id,
    bool? IsActive = null
);

/// <summary>
/// Kontroler API dla panelu administratora do pełnego zarządzania bazą utworów (CRUD).
/// Umożliwia dodawanie, edycję, usuwanie, przywracanie oraz przeglądanie wszystkich utworów (w tym nieaktywnych).
/// </summary>
[Route("api/admin/tracks")]
[ApiController]
[Authorize(Roles = "admin,head_admin")]
[Produces("application/json")]
public class AdminTracksController : ControllerBase
{
    private readonly schoolmusicContext _context;

    public AdminTracksController(schoolmusicContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Pobiera listę utworów dla panelu administratora z możliwością filtrowania po statusie aktywności,
    /// wyszukiwania, paginacji i sortowania.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<AdminTracksPagedResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAdminTracks(
        [FromQuery] string? search = null,
        [FromQuery] string? genre = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sortBy = "id",
        [FromQuery] string? sortOrder = "asc")
    {
        var query = _context.Songs
            .AsNoTracking()
            .Include(s => s.Artist)
            .AsQueryable();

        if (isActive.HasValue)
        {
            query = query.Where(s => s.IsActive == isActive.Value);
        }

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

        var order = sortOrder?.ToLowerInvariant() == "desc" ? "desc" : "asc";
        query = (sortBy?.ToLowerInvariant(), order) switch
        {
            ("id", "desc") => query.OrderByDescending(s => s.Id),
            ("id", "asc") => query.OrderBy(s => s.Id),
            ("title", "desc") => query.OrderByDescending(s => s.Title),
            ("title", "asc") => query.OrderBy(s => s.Title),
            ("artist", "desc") => query.OrderByDescending(s => s.Artist.Name).ThenBy(s => s.Title),
            ("artist", "asc") => query.OrderBy(s => s.Artist.Name).ThenBy(s => s.Title),
            ("duration", "desc") => query.OrderByDescending(s => s.DurationMs).ThenBy(s => s.Title),
            ("duration", "asc") => query.OrderBy(s => s.DurationMs).ThenBy(s => s.Title),
            ("newest", "asc") => query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Title),
            ("newest", "desc") => query.OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Title),
            _ => query.OrderBy(s => s.Id)
        };

        int total = await query.CountAsync();
        int safePage = Math.Max(1, page);
        int safePageSize = Math.Clamp(pageSize, 1, 100);
        int totalPages = (int)Math.Ceiling(total / (double)safePageSize);

        var songsPage = await query
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync();

        var songIds = songsPage.Select(s => s.Id).ToList();

        var playCounts = await _context.Histories
            .Where(h => songIds.Contains(h.SongId))
            .GroupBy(h => h.SongId)
            .Select(g => new { SongId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SongId, x => x.Count);

        var queueVotes = await _context.QueueItems
            .Where(q => songIds.Contains(q.SongId) && (q.Status == "queued" || q.Status == "playing"))
            .GroupBy(q => q.SongId)
            .Select(g => new { SongId = g.Key, Votes = g.SelectMany(qi => qi.Votes).Count() })
            .ToDictionaryAsync(x => x.SongId, x => x.Votes);

        var items = songsPage.Select(s => new AdminTrackItemDto(
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
            IsActive: s.IsActive,
            CreatedAt: s.CreatedAt,
            TotalPlaysCount: playCounts.GetValueOrDefault(s.Id, 0),
            CurrentQueueVotesCount: queueVotes.GetValueOrDefault(s.Id, 0)
        )).ToList();

        return Ok(new AdminTracksPagedResponseDto(
            Total: total,
            Page: safePage,
            PageSize: safePageSize,
            TotalPages: totalPages,
            Items: items
        ));
    }

    /// <summary>
    /// Pobiera szczegóły pojedynczego utworu dla panelu administratora (w tym utworu nieaktywnego).
    /// </summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType<AdminTrackItemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAdminTrackById(int id)
    {
        var song = await _context.Songs
            .Include(s => s.Artist)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (song == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono utworu o podanym ID.");
        }

        int totalPlays = await _context.Histories.CountAsync(h => h.SongId == id);
        int currentVotes = await _context.QueueItems
            .Where(q => q.SongId == id && (q.Status == "queued" || q.Status == "playing"))
            .SelectMany(q => q.Votes)
            .CountAsync();

        var dto = new AdminTrackItemDto(
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
            IsActive: song.IsActive,
            CreatedAt: song.CreatedAt,
            TotalPlaysCount: totalPlays,
            CurrentQueueVotesCount: currentVotes
        );

        return Ok(dto);
    }

    /// <summary>
    /// Dodaje nowy utwór do bazy. Jeśli podany wykonawca nie istnieje, zostaje automatycznie utworzony.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<AdminTrackItemDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateTrack([FromBody] CreateTrackDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Title))
        {
            return this.Create(StatusCodes.Status400BadRequest, "Tytuł utworu jest wymagany.");
        }

        if (string.IsNullOrWhiteSpace(dto.ArtistName))
        {
            return this.Create(StatusCodes.Status400BadRequest, "Nazwa wykonawcy jest wymagana.");
        }

        if (dto.DurationMs <= 0)
        {
            return this.Create(StatusCodes.Status400BadRequest, "Czas trwania utworu musi być większy od zera.");
        }

        string? normalizedSpotifyId = string.IsNullOrWhiteSpace(dto.SpotifyId)
            ? null
            : SpotifyTrackImporter.NormalizeSpotifyTrackId(dto.SpotifyId);

        if (normalizedSpotifyId != null)
        {
            var duplicateSong = await _context.Songs
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SpotifyId == normalizedSpotifyId);
            if (duplicateSong != null)
            {
                return this.Create(
                    StatusCodes.Status409Conflict,
                    $"Utwór o identyfikatorze Spotify '{normalizedSpotifyId}' już istnieje w bazie (ID: {duplicateSong.Id}).",
                    extensions: new Dictionary<string, object?> { ["existingSongId"] = duplicateSong.Id });
            }
        }

        var trimmedArtistName = dto.ArtistName.Trim();
        var artist = await _context.Artists
            .FirstOrDefaultAsync(a => a.Name.ToLower() == trimmedArtistName.ToLower());

        if (artist == null)
        {
            artist = new Artist
            {
                Name = trimmedArtistName,
                Source = string.IsNullOrWhiteSpace(dto.Source) ? "other" : dto.Source.Trim()
            };
            _context.Artists.Add(artist);
            await _context.SaveChangesAsync();
        }

        var song = new Song
        {
            Title = dto.Title.Trim(),
            ArtistId = artist.Id,
            Genre = string.IsNullOrWhiteSpace(dto.Genre) ? null : dto.Genre.Trim(),
            DurationMs = dto.DurationMs,
            SongUrl = string.IsNullOrWhiteSpace(dto.SongUrl) ? null : dto.SongUrl.Trim(),
            Cover = string.IsNullOrWhiteSpace(dto.Cover) ? null : dto.Cover.Trim(),
            Source = string.IsNullOrWhiteSpace(dto.Source) ? "other" : dto.Source.Trim(),
            SpotifyId = normalizedSpotifyId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.Songs.Add(song);
        await _context.SaveChangesAsync();

        var responseDto = new AdminTrackItemDto(
            Id: song.Id,
            Title: song.Title,
            ArtistId: artist.Id,
            ArtistName: artist.Name,
            Cover: song.Cover,
            DurationMs: song.DurationMs,
            Genre: song.Genre,
            Source: song.Source,
            SongUrl: song.SongUrl,
            SpotifyId: song.SpotifyId,
            IsActive: song.IsActive,
            CreatedAt: song.CreatedAt,
            TotalPlaysCount: 0,
            CurrentQueueVotesCount: 0
        );

        return CreatedAtAction(nameof(GetAdminTrackById), new { id = song.Id }, responseDto);
    }

    /// <summary>
    /// Aktualizuje metadane istniejącego utworu. W razie zmiany nazwy wykonawcy, automatycznie tworzy lub łączy artystę.
    /// </summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType<AdminTrackItemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateTrack(int id, [FromBody] UpdateTrackDto dto)
    {
        var song = await _context.Songs
            .Include(s => s.Artist)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (song == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono utworu o podanym ID.");
        }

        if (dto.Title != null)
        {
            if (string.IsNullOrWhiteSpace(dto.Title))
            {
                return this.Create(StatusCodes.Status400BadRequest, "Tytuł utworu nie może być pusty.");
            }
            song.Title = dto.Title.Trim();
        }

        if (dto.ArtistName != null)
        {
            if (string.IsNullOrWhiteSpace(dto.ArtistName))
            {
                return this.Create(StatusCodes.Status400BadRequest, "Nazwa wykonawcy nie może być pusta.");
            }

            var trimmedArtistName = dto.ArtistName.Trim();
            if (song.Artist == null || song.Artist.Name.ToLower() != trimmedArtistName.ToLower())
            {
                var artist = await _context.Artists
                    .FirstOrDefaultAsync(a => a.Name.ToLower() == trimmedArtistName.ToLower());

                if (artist == null)
                {
                    artist = new Artist
                    {
                        Name = trimmedArtistName,
                        Source = string.IsNullOrWhiteSpace(dto.Source) ? song.Source : dto.Source.Trim()
                    };
                    _context.Artists.Add(artist);
                    await _context.SaveChangesAsync();
                }

                song.ArtistId = artist.Id;
                song.Artist = artist;
            }
        }

        if (dto.DurationMs.HasValue)
        {
            if (dto.DurationMs.Value <= 0)
            {
                return this.Create(StatusCodes.Status400BadRequest, "Czas trwania utworu musi być większy od zera.");
            }
            song.DurationMs = dto.DurationMs.Value;
        }

        if (dto.SpotifyId != null)
        {
            string? normalizedSpotifyId = string.IsNullOrWhiteSpace(dto.SpotifyId)
                ? null
                : SpotifyTrackImporter.NormalizeSpotifyTrackId(dto.SpotifyId);

            if (normalizedSpotifyId != null)
            {
                var conflictSongId = await _context.Songs
                    .AsNoTracking()
                    .Where(s => s.Id != id && s.SpotifyId == normalizedSpotifyId)
                    .Select(s => (int?)s.Id)
                    .FirstOrDefaultAsync();
                if (conflictSongId.HasValue)
                {
                    return this.Create(
                        StatusCodes.Status409Conflict,
                        $"Identyfikator Spotify '{normalizedSpotifyId}' jest już przypisany do utworu ID {conflictSongId.Value}.");
                }
            }

            song.SpotifyId = normalizedSpotifyId;
        }

        if (dto.Genre != null) song.Genre = string.IsNullOrWhiteSpace(dto.Genre) ? null : dto.Genre.Trim();
        if (dto.SongUrl != null) song.SongUrl = string.IsNullOrWhiteSpace(dto.SongUrl) ? null : dto.SongUrl.Trim();
        if (dto.Cover != null) song.Cover = string.IsNullOrWhiteSpace(dto.Cover) ? null : dto.Cover.Trim();
        if (dto.Source != null) song.Source = string.IsNullOrWhiteSpace(dto.Source) ? "other" : dto.Source.Trim();
        if (dto.IsActive.HasValue) song.IsActive = dto.IsActive.Value;

        await _context.SaveChangesAsync();

        var responseDto = new AdminTrackItemDto(
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
            IsActive: song.IsActive,
            CreatedAt: song.CreatedAt,
            TotalPlaysCount: await _context.Histories.CountAsync(h => h.SongId == id),
            CurrentQueueVotesCount: await _context.QueueItems
                .Where(q => q.SongId == id && (q.Status == "queued" || q.Status == "playing"))
                .SelectMany(q => q.Votes)
                .CountAsync()
        );

        return Ok(responseDto);
    }

    /// <summary>
    /// Usuwa utwór lub wykonuje bezpieczną dezaktywację (soft-delete).
    /// Jeśli utwór jest powiązany z historią lub kolejką, system zawsze wykonuje soft-delete (IsActive = false).
    /// </summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType<AdminTrackStatusResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTrack(int id, [FromQuery] bool hardDelete = false)
    {
        var song = await _context.Songs.FirstOrDefaultAsync(s => s.Id == id);
        if (song == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono utworu o podanym ID.");
        }

        bool hasReferences = await _context.QueueItems.AnyAsync(q => q.SongId == id)
            || await _context.Histories.AnyAsync(h => h.SongId == id)
            || await _context.DefaultSongs.AnyAsync(d => d.SongId == id);

        if (hardDelete && !hasReferences)
        {
            _context.Songs.Remove(song);
            await _context.SaveChangesAsync();
            return Ok(new AdminTrackStatusResponseDto(
                Message: "Utwór został trwale usunięty z bazy danych.",
                Id: id
            ));
        }

        song.IsActive = false;
        await _context.SaveChangesAsync();

        return Ok(new AdminTrackStatusResponseDto(
            Message: "Utwór został pomyślnie dezaktywowany (soft-delete).",
            Id: id,
            IsActive: false
        ));
    }

    /// <summary>
    /// Przywraca wcześniej dezaktywowany utwór (IsActive = true).
    /// </summary>
    [HttpPost("{id:int}/restore")]
    [ProducesResponseType<AdminTrackStatusResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RestoreTrack(int id)
    {
        var song = await _context.Songs.FirstOrDefaultAsync(s => s.Id == id);
        if (song == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono utworu o podanym ID.");
        }

        song.IsActive = true;
        await _context.SaveChangesAsync();

        return Ok(new AdminTrackStatusResponseDto(
            Message: "Utwór został pomyślnie przywrócony.",
            Id: id,
            IsActive: true
        ));
    }
}
