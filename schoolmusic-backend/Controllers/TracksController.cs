using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LibVLCSharp.Shared;
using schoolmusic_backend.Models;
using schoolmusic_backend.Extensions;
using static System.Net.WebRequestMethods;
using schoolmusic_backend.Services;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class TracksController : ControllerBase
{
    private readonly schoolmusicContext _context;
    private readonly IBreakService _breakService;
    public TracksController(schoolmusicContext context, IBreakService breakService)
    {
        _context = context;
        _breakService = breakService;
    }

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
    /// <param name="id">Id piosenki w bazie danych</param>
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
        var song = await _context.Songs.FindAsync(id);

        if (song == null)
        {
            // ! tutaj trzeba zrobić podłączenie do spotify web api a później dodać do bazy danych
            // await Songs.Add(song);
            // await _context.SaveChangesAsync();
            // na razie NotFound
            return NotFound(new
            {
                Message = "Nie znaleziono piosenki (dołącz SPotify API póxniej"
            });
        }
        if (user == null)
        {
            return NotFound(new
            {
                Message = "Użytkownik nie znaleziony"
            });
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

        var song = await _context.Songs
            .FirstOrDefaultAsync(s => s.SpotifyId == spotifyId);

        if (user == null)
        {
            return NotFound(new
            {
                Message = "Nie znaleziono użytkownika o podanym ID"
            });
        }
        if (song == null)
        {
            return NotFound(new
            {
                Message = "Nie znaleziono piosenki o tym ID"
            });
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
    [HttpGet("play-song")]
    [AllowAnonymous]
    // Testowa funkcja do odtworzenia piosenki na Twoim Windowsie
    // url https://www.soundhelix.com/examples/mp3/SoundHelix-Song-1.mp3
    // url3 https://www.soundhelix.com/examples/mp3/SoundHelix-Song-4.mp3
    public async Task<IActionResult> PlaySong([FromQuery] string url)
    {
        string targetUrl = string.IsNullOrWhiteSpace(url)
                ? "https://www.soundhelix.com/examples/mp3/SoundHelix-Song-1.mp3"
                : url;

        // 1. Pobieramy plik do pliku tymczasowego na dysku
        // Rozwiązuje problem desynchronizacji zegara imem i braku seekowania
        string tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp3");

        try
        {
            using (var httpClient = new HttpClient())
            {
                var bytes = await httpClient.GetByteArrayAsync(targetUrl);
                await System.IO.File.WriteAllBytesAsync(tempFilePath, bytes);
            }

            // 2. Opcje dla LibVLC: zwiększony bufor i synchronizacja audio
            string[] options = new[]
            {
            "--file-caching=2000",
            "--network-caching=2000",
            "--clock-jitter=0",
            "--aout=directsound" // alternatywny backend audio pod Windows, zapobiegający problemom z wasapi
        };

            using var libvlc = new LibVLCSharp.Shared.LibVLC(enableDebugLogs: false, options);
            using var media = new Media(libvlc, tempFilePath, FromType.FromPath);
            using var mediaPlayer = new MediaPlayer(media);

            mediaPlayer.Play();
        }
        finally
        {
            // Sprzątanie pliku po odtworzeniu
            if (System.IO.File.Exists(tempFilePath))
            {
                try { System.IO.File.Delete(tempFilePath); } catch { }
            }
        }

        return Ok(new { 
            Message = "Zagrano piosenke",
            Url = targetUrl
        });
    }

    [HttpGet("testing-websockets")]
    [AllowAnonymous]
    public async Task<IActionResult> getRanks(int breakId)
    {
        var breakEntity = await _context.Breaks.FindAsync(breakId);
        if (breakEntity == null) return Ok(new { Message = "Nie znaleziono przerwy o podanym id" });

        int breakDurationSeconds = 0;
        if(breakEntity.EndsAt.HasValue && breakEntity.EndsAt.Value > breakEntity.StartAt)
        {
            breakDurationSeconds = (int)(breakEntity.EndsAt.Value - breakEntity.StartAt).TotalSeconds;
        }
        else
        {
            return Problem(
                detail: "Przerwa nie ma ustawionego poprawnie czasu",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Błąd przerwy"
            );
        }
        var approvedQueue = await _context.QueueItems
            .Where(q => q.BreakId == breakId && (q.ModerationStatus == "approved" || q.ModerationStatus == null))
            .Include(q => q.Song)
                .ThenInclude(s => s.Artist)
            .Include(q => q.Votes)
            .ToListAsync();

        if (!approvedQueue.Any())
        {
            return Ok(new
            {
                Message = "Brak utworów w kolejce na tę przerwę.",
                BreakId = breakId,
                DurationSeconds = breakDurationSeconds,
                Tracks = Array.Empty<object>()
            });
        }

        var breakPlan = _breakService.CalculateBreak(breakId, breakDurationSeconds, approvedQueue);
        return Ok(breakPlan);
    }
}