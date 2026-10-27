using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;
using schoolmusic_backend.Extensions;

[Route("api/breaks")]
[ApiController]
[Authorize]
public class RecessController : ControllerBase
{
    private readonly schoolmusicContext _context;

    public RecessController(schoolmusicContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Reprezentuje odpowiedź zawierającą dane dotyczące przerwy
    /// </summary>
    public record BreakResponseDto(
        int Id, 
        int BreakNumber, 
        DateTime StartAt, 
        DateTime EndsAt, 
        DateTime? VoteEnds,
        bool IsMuted = false,
        string? MuteReason = null
    );
    /// <summary>
    /// Dane transferu do aktualizacji wpisu harmonogramu przerwy
    /// </summary>
    public record UpdateBreakScheduleItemDto(int Id, TimeOnly StartAt, TimeOnly EndsAt, TimeOnly? VoteEnds = null);
    /// <summary>
    /// Dane potrzebne do utworzenia dnia wyjątkowego
    /// </summary>
    public record CreateExceptionDayDto(DateOnly StartDate, DateOnly? EndDate, string? DayType, string? Description);
    /// <summary>
    /// Dane potrzebne do utworzenia przerwy wyjątkowej
    /// </summary>
    public record CreateExceptionBreakDto(DateOnly BreakDate, sbyte BreakNumber, bool MusicEnabled = false, string? Description = null);

    /// <summary>
    /// Pobiera harmonogram przerw na dany dzień z informacją 
    /// czy obowiązuje ExceptionDay lub ExceptionBreak.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetBreaks([FromQuery] DateOnly? date)
    {
        var targetDate = date ?? DateOnly.FromDateTime(DateTime.Today);

        // check if its exception date
        var exceptionDay = await _context.ExceptionDays
            .Where(e => e.StartDate <= targetDate && targetDate <= e.EndDate)
            .FirstOrDefaultAsync();

        bool isExceptionDay = exceptionDay != null;

        // get all exception breaks
        var exceptionBreaks = await _context.ExceptionBreaks
            .Where(eb => eb.BreakDate == targetDate)
            .ToListAsync();

        var defaultBreaks = await _context.Breaks
            .Where(b => b.BreakDate == targetDate)
            .OrderBy(b => b.BreakNumber)
            .ToListAsync();

        if (defaultBreaks.Count == 0)
        {
            // Fallback: jeśli brak przerw wygenerowanych na dzisiejszą datę, pobierz przerwy bazowe
            defaultBreaks = await _context.Breaks
                .OrderBy(b => b.BreakNumber)
                .Take(8)
                .ToListAsync();
        }

        var breakList = defaultBreaks.Select(b =>
        {
            var startAt = targetDate.ToDateTime(TimeOnly.FromDateTime(b.StartAt));
            var endsAt = targetDate.ToDateTime(TimeOnly.FromDateTime(b.EndsAt));

            // check if this break is exception break
            var matchingExceptionBreak = exceptionBreaks.FirstOrDefault(eb =>
                eb.BreakNumber == b.BreakNumber
            );

            bool isMuted = isExceptionDay || (matchingExceptionBreak != null && !matchingExceptionBreak.MusicEnabled);
            string? muteReason = isExceptionDay 
                ? exceptionDay?.Description 
                : matchingExceptionBreak?.Description;

            return new BreakResponseDto(
                Id: b.Id,
                BreakNumber: b.BreakNumber,
                StartAt: startAt,
                EndsAt: endsAt,
                VoteEnds: b.VoteEnds.HasValue ? targetDate.ToDateTime(TimeOnly.FromDateTime(b.VoteEnds.Value)) : null,
                IsMuted: isMuted,
                MuteReason: muteReason
            );
        }).ToList();

        return Ok(new
        {
            Date = targetDate,
            IsExceptionDay = isExceptionDay,
            IsMusicEnabled = !isExceptionDay && breakList.Any(b => !b.IsMuted),
            ExceptionDescription = exceptionDay?.Description,
            Message = isExceptionDay
                ? $"W dniu {targetDate:yyyy-MM-dd} obowiązuje dzień bez muzyki ({exceptionDay?.Description})."
                : $"Plan przerw na dzień {targetDate:yyyy-MM-dd}.",
            Przerwy = breakList,
            WyjatkowePrzerwy = exceptionBreaks
        });
    }

    /// <summary>
    /// Sprawdza bieżący status: czy trwa przerwa, 
    /// czy muzyka ma grać, czy obowiązuje ExceptionDay 
    /// lub ExceptionBreak.
    /// </summary>
    [HttpGet("current-status")]
    [AllowAnonymous]
    public async Task<IActionResult> GetCurrentStatus()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        // check if today is exception day
        var activeExceptionDay = await _context.ExceptionDays
            .Where(e => e.StartDate <= today && today <= e.EndDate)
            .FirstOrDefaultAsync();

        if (activeExceptionDay != null)
        {
            return Ok(new
            {
                CurrentTime = now,
                IsExceptionDay = true,
                IsExceptionBreak = false,
                IsMusicEnabled = false,
                IsBreakActive = false,
                MuteReason = activeExceptionDay.Description,
                Message = $"Aktualnie trwa dzień wyjątkowy: {activeExceptionDay.Description}. Muzyka nie gra.",
                ActiveBreak = (BreakResponseDto?)null,
                NextBreak = (BreakResponseDto?)null
            });
        }

        // check exception breaks for today
        var todayExceptionBreaks = await _context.ExceptionBreaks
            .Where(eb => eb.BreakDate == today)
            .ToListAsync();

        var defaultBreaks = await _context.Breaks
            .Where(b => b.BreakDate == today)
            .OrderBy(b => b.BreakNumber)
            .ToListAsync();

        var todayBreaks = defaultBreaks.Select(b =>
        {
            var startAt = today.ToDateTime(TimeOnly.FromDateTime(b.StartAt));
            var endsAt = today.ToDateTime(TimeOnly.FromDateTime(b.EndsAt));

            var matchingExceptionBreak = todayExceptionBreaks.FirstOrDefault(eb => eb.BreakNumber == b.BreakNumber);
            bool isMuted = matchingExceptionBreak != null && !matchingExceptionBreak.MusicEnabled;

            return new BreakResponseDto(
                Id: b.Id,
                BreakNumber: b.BreakNumber,
                StartAt: startAt,
                EndsAt: endsAt,
                VoteEnds: b.VoteEnds.HasValue ? today.ToDateTime(TimeOnly.FromDateTime(b.VoteEnds.Value)) : null,
                IsMuted: isMuted,
                MuteReason: matchingExceptionBreak?.Description
            );
        }).ToList();

        var activeBreak = todayBreaks.FirstOrDefault(b => now >= b.StartAt && now <= b.EndsAt);
        var nextBreak = todayBreaks.Where(b => b.StartAt > now).OrderBy(b => b.StartAt).FirstOrDefault();

        var activeExceptionBreak = activeBreak != null
            ? todayExceptionBreaks.FirstOrDefault(eb => eb.BreakNumber == activeBreak.BreakNumber && !eb.MusicEnabled)
            : null;

        bool isMusicPlaying = activeBreak != null && activeExceptionBreak == null;

        string message;
        if (activeBreak != null)
        {
            if (activeExceptionBreak != null)
            {
                message = $"Trwa przerwa nr {activeBreak.BreakNumber}, ale muzyka jest wyciszona ({activeExceptionBreak.Description}).";
            }
            else
            {
                message = $"Trwa przerwa nr {activeBreak.BreakNumber} (do {activeBreak.EndsAt:HH:mm}). Muzyka gra!";
            }
        }
        else if (nextBreak != null)
        {
            message = $"Kolejna przerwa nr {nextBreak.BreakNumber} rozpocznie się o {nextBreak.StartAt:HH:mm}.";
        }
        else
        {
            message = "Wszystkie przerwy na dziś dobiegły końca.";
        }

        return Ok(new
        {
            CurrentTime = now,
            IsExceptionDay = false,
            IsExceptionBreak = activeExceptionBreak != null,
            IsBreakActive = activeBreak != null,
            IsMusicEnabled = isMusicPlaying,
            MuteReason = activeExceptionBreak?.Description,
            ActiveBreak = activeBreak,
            NextBreak = nextBreak,
            Message = message
        });
    }

    /// <summary>
    /// Pobiera listę wszystkich zaplanowanych dni 
    /// wyjątkowych (cały dzień bez muzyki).
    /// </summary>
    [HttpGet("exceptions")]
    public async Task<IActionResult> GetAllExceptionDays()
    {
        var exceptions = await _context.ExceptionDays
            .OrderByDescending(e => e.StartDate)
            .ToListAsync();

        return Ok(new
        {
            Message = "Lista dni wyjątkowych",
            Exceptions = exceptions
        });
    }

    /// <summary>
    /// Dodaje dzień wyjątkowy.
    /// Tylko dla administratora.
    /// </summary>
    [HttpPost("exceptions")]
    public async Task<IActionResult> CreateExceptionDay([FromBody] CreateExceptionDayDto dto)
    {
        int userId = HttpContext.GetCurrentUserId() ?? 0;
        if (!await _context.IsAdminAsync(userId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Message = "Tylko administrator może zarządzać dniami wyjątkowymi"
            });
        }

        var endDate = dto.EndDate ?? dto.StartDate;
        if (endDate < dto.StartDate)
        {
            return BadRequest(new
            {
                Message = "Data zakończenia musi być późniejsza lub równa dacie rozpoczęcia"
            });
        }

        var exceptionDay = new ExceptionDay
        {
            StartDate = dto.StartDate,
            EndDate = endDate,
            DayType = string.IsNullOrWhiteSpace(dto.DayType) ? "no_music" : dto.DayType,
            Description = dto.Description
        };

        _context.ExceptionDays.Add(exceptionDay);
        await _context.SaveChangesAsync();

        return Ok(new
        {
            Message = "Pomyślnie dodano dzień wyjątkowy",
            ExceptionDay = exceptionDay
        });
    }

    /// <summary>
    /// Usuwa dzień wyjątkowy o podanym identyfikatorze. 
    /// Tylko dla administratora.
    /// </summary>
    [HttpDelete("exceptions/{id:int}")]
    public async Task<IActionResult> DeleteExceptionDay(int id)
    {
        int userId = HttpContext.GetCurrentUserId() ?? 0;
        if (!await _context.IsAdminAsync(userId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Message = "Tylko administrator może usuwać dni wyjątkowe"
            });
        }

        var exceptionDay = await _context.ExceptionDays.FindAsync(id);
        if (exceptionDay == null)
        {
            return NotFound(new { Message = "Nie znaleziono dnia wyjątkowego o podanym ID" });
        }

        _context.ExceptionDays.Remove(exceptionDay);
        await _context.SaveChangesAsync();

        return Ok(new { Message = "Pomyślnie usunięto dzień wyjątkowy" });
    }


    /// <summary>
    /// Pobiera listę zaplanowanych wyciszeń 
    /// konkretnych przerw (ExceptionBreak).
    /// </summary>
    [HttpGet("exception-breaks")]
    public async Task<IActionResult> GetAllExceptionBreaks()
    {
        var exceptionBreaks = await _context.ExceptionBreaks
            .OrderByDescending(eb => eb.BreakDate)
            .ThenBy(eb => eb.BreakNumber)
            .ToListAsync();

        return Ok(new
        {
            Message = "Lista wyciszonych przerw (wyjątków)",
            ExceptionBreaks = exceptionBreaks
        });
    }

    /// <summary>
    /// Dodaje wyciszenie konkretnej przerwy.
    /// Tylko dla administratora.
    /// </summary>
    [HttpPost("exception-breaks")]
    public async Task<IActionResult> CreateExceptionBreak([FromBody] CreateExceptionBreakDto dto)
    {
        int userId = HttpContext.GetCurrentUserId() ?? 0;
        if (!await _context.IsAdminAsync(userId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Message = "Tylko administrator może zarządzać przerwami wyjątkowymi"
            });
        }

        var exceptionBreak = new ExceptionBreak
        {
            BreakDate = dto.BreakDate,
            BreakNumber = dto.BreakNumber,
            MusicEnabled = dto.MusicEnabled,
            Description = dto.Description
        };

        _context.ExceptionBreaks.Add(exceptionBreak);
        await _context.SaveChangesAsync();

        return Ok(new
        {
            Message = "Pomyślnie dodano przerwę z wyciszeniem muzyki",
            ExceptionBreak = exceptionBreak
        });
    }

    /// <summary>
    /// Usuwa wyciszenie konkretnej przerwy.
    /// Tylko dla administratora.
    /// </summary>
    [HttpDelete("exception-breaks/{id:int}")]
    public async Task<IActionResult> DeleteExceptionBreak(int id)
    {
        int userId = HttpContext.GetCurrentUserId() ?? 0;
        if (!await _context.IsAdminAsync(userId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Message = "Tylko administrator może usuwać wyjątki przerw"
            });
        }

        var exceptionBreak = await _context.ExceptionBreaks.FindAsync(id);
        if (exceptionBreak == null)
        {
            return NotFound(new { Message = "Nie znaleziono wyjątku przerwy o podanym ID" });
        }

        _context.ExceptionBreaks.Remove(exceptionBreak);
        await _context.SaveChangesAsync();

        return Ok(new { Message = "Pomyślnie usunięto wyjątek przerwy" });
    }


    /// <summary>
    /// Pozwala administratorowi na edycję stałych godzin 
    /// przerw w tabeli break (np. zmiana dzwonków w szkole).
    /// </summary>
    [HttpPut("update-break")]
    public async Task<IActionResult> UpdateDefaultBreaks([FromBody] List<UpdateBreakScheduleItemDto> items)
    {
        int userId = HttpContext.GetCurrentUserId() ?? 0;
        if (!await _context.IsAdminAsync(userId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Message = "Tylko administrator może edytować rozkład przerw"
            });
        }

        if (items == null || items.Count == 0)
        {
            return BadRequest(new { Message = "Lista przerw nie może być pusta" });
        }

        var baseDate = new DateOnly(2026, 1, 1);

        foreach (var item in items)
        {
            var breakEntity = await _context.Breaks.FindAsync(item.Id);
            if (breakEntity != null)
            {
                breakEntity.StartAt = baseDate.ToDateTime(item.StartAt);
                breakEntity.EndsAt = baseDate.ToDateTime(item.EndsAt);
                if (item.VoteEnds.HasValue)
                {
                    breakEntity.VoteEnds = baseDate.ToDateTime(item.VoteEnds.Value);
                }
            }
        }

        await _context.SaveChangesAsync();

        return Ok(new
        {
            Message = "Rozkład przerw w bazie został zaktualizowany"
        });
    }
}