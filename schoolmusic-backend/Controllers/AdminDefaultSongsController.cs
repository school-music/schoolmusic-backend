using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Controllers
{
    public record DefaultSongItemDto(
        int Id,
        int SongId,
        string Title,
        int ArtistId,
        string ArtistName,
        int DurationMs,
        string? Cover,
        string? Genre,
        string? SongUrl,
        bool IsActive,
        string? SpotifyId = null,
        string Source = "other"
    );

    public record AddDefaultSongDto(int SongId);

    public sealed record RemoveDefaultSongResponseDto(
        string Message,
        int Id,
        int SongId
    );

    /// <summary>
    /// Kontroler panelu administracyjnego do zarządzania listą utworów domyślnych (fallback, gdy kolejka jest pusta).
    /// </summary>
    [Route("api/admin/default-songs")]
    [ApiController]
    [Authorize(Roles = "admin,head_admin")]
    [Produces("application/json")]
    public class AdminDefaultSongsController : ControllerBase
    {
        private readonly schoolmusicContext _context;

        public AdminDefaultSongsController(schoolmusicContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Pobiera listę wszystkich utworów domyślnych wraz z ich metadanymi.
        /// </summary>
        [HttpGet]
        [ProducesResponseType<IReadOnlyList<DefaultSongItemDto>>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> GetDefaultSongs()
        {
            var items = await _context.DefaultSongs
                .Include(d => d.Song)
                    .ThenInclude(s => s.Artist)
                .OrderBy(d => d.Id)
                .Select(d => new DefaultSongItemDto(
                    d.Id,
                    d.SongId,
                    d.Song.Title,
                    d.Song.ArtistId,
                    d.Song.Artist != null ? d.Song.Artist.Name : "Nieznany wykonawca",
                    d.Song.DurationMs,
                    d.Song.Cover,
                    d.Song.Genre,
                    d.Song.SongUrl,
                    d.Song.IsActive,
                    d.Song.SpotifyId,
                    d.Song.Source
                ))
                .ToListAsync();

            return Ok(items);
        }

        /// <summary>
        /// Dodaje utwór do listy utworów domyślnych.
        /// </summary>
        [HttpPost]
        [ProducesResponseType<DefaultSongItemDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> AddDefaultSong([FromBody] AddDefaultSongDto dto)
        {
            var song = await _context.Songs.Include(s => s.Artist).FirstOrDefaultAsync(s => s.Id == dto.SongId);
            if (song == null)
            {
                return this.Create(StatusCodes.Status404NotFound, "Utwór o podanym ID nie istnieje.");
            }

            if (!song.IsActive)
            {
                return this.Create(StatusCodes.Status400BadRequest, "Nie można dodać nieaktywnego utworu do listy domyślnej.");
            }

            bool alreadyExists = await _context.DefaultSongs.AnyAsync(d => d.SongId == dto.SongId);
            if (alreadyExists)
            {
                return this.Create(StatusCodes.Status409Conflict, "Ten utwór znajduje się już na liście utworów domyślnych.");
            }

            var defaultSong = new DefaultSong
            {
                SongId = dto.SongId
            };

            _context.DefaultSongs.Add(defaultSong);
            await _context.SaveChangesAsync();

            var resultDto = new DefaultSongItemDto(
                defaultSong.Id,
                song.Id,
                song.Title,
                song.ArtistId,
                song.Artist?.Name ?? "Nieznany wykonawca",
                song.DurationMs,
                song.Cover,
                song.Genre,
                song.SongUrl,
                song.IsActive,
                song.SpotifyId,
                song.Source
            );

            return CreatedAtAction(nameof(GetDefaultSongs), new { id = defaultSong.Id }, resultDto);
        }

        /// <summary>
        /// Usuwa utwór z listy utworów domyślnych po identyfikatorze wpisu lub identyfikatorze utworu.
        /// </summary>
        [HttpDelete("{id:int}")]
        [ProducesResponseType<RemoveDefaultSongResponseDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> RemoveDefaultSong(int id)
        {
            var defaultSong = await _context.DefaultSongs
                .Include(d => d.Song)
                .FirstOrDefaultAsync(d => d.Id == id || d.SongId == id);

            if (defaultSong == null)
            {
                return this.Create(StatusCodes.Status404NotFound, "Wpis domyślnego utworu nie został znaleziony.");
            }

            _context.DefaultSongs.Remove(defaultSong);
            await _context.SaveChangesAsync();

            return Ok(new RemoveDefaultSongResponseDto(
                Message: $"Pomyślnie usunięto utwór '{defaultSong.Song?.Title ?? id.ToString()}' z listy utworów domyślnych.",
                Id: defaultSong.Id,
                SongId: defaultSong.SongId
            ));
        }
    }
}
