using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NAudio.Wave;
using Pomelo.EntityFrameworkCore.MySql.Query.Internal;
using schoolmusic_backend.Models;
using schoolmusic_backend.Extensions;
using static System.Net.WebRequestMethods;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class TracksController : ControllerBase
{
    private readonly schoolmusicContext _context;
    public TracksController(schoolmusicContext context)
    {
        _context = context;
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

        if(song == null)
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
        if(user == null)
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

        if(user == null)
        {
            return NotFound(new
            {
                Message = "Nie znaleziono użytkownika o podanym ID"
            });
        }
        if(song == null)
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
            .FirstOrDefaultAsync(u => u.Id ==  currentUserId);
        var song = await _context.Songs.FirstOrDefaultAsync(s => s.Id == id);
        if(user == null)
        {
            return NotFound(new
            {
                Message = "Nie znaleziono użytkownika"
            });
        }
        if(song == null)
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
    public async Task<IActionResult> PlaySong([FromQuery] string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return BadRequest(new
            {
                Message = "Brakujący argument url w zapytaniu"
            });
        }

        try
        {
            using (var mf = new MediaFoundationReader(url)) // odtwarzanie z URL
            using (var wo = new WasapiPlayerBuilder().WithDefaultDeviceStreamRouting().Build()) // domyślne urządzenie audio
            {
                wo.Init(mf);
                wo.Play();

                //
                int sekundy = 0;
                while (wo.PlaybackState == PlaybackState.Playing && sekundy < 15)
                {
                    await Task.Delay(1000);
                    sekundy++;
                }

                wo.Stop();
            }

            return Ok(new { Message = "Test odtwarzania zakończony pomyślnie!" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
