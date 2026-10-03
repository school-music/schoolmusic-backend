using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;

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

    public record BreakResponseDto(
        int Id, 
        int BreakNumber, 
        DateTime StartAt, 
        DateTime EndsAt, 
        DateTime? VoteEnds,
        bool IsMuted = false,
        string? MuteReason = null
    );
    public record UpdateBreakScheduleItemDto(int Id, TimeOnly StartAt, TimeOnly EndsAt, TimeOnly? VoteEnds = null);
    public record CreateExceptionDayDto(DateTime StartAt, DateTime? EndsAt, string? Description);
    public record CreateExceptionBreakDto(DateTime StartsAt, DateTime? EndsAt, string? Description);

    /// <summary>
    /// Pobiera harmonogram przerw na dany dzień z informacją 
    /// czy obowiązuje ExceptionDay lub ExceptionBreak.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetBreaks([FromQuery] DateOnly? date)
    {
        var targetDate = date ?? DateOnly.FromDateTime(DateTime.Today);
        var dayStart = targetDate.ToDateTime(TimeOnly.MinValue);
        var dayEnd = targetDate.ToDateTime(TimeOnly.MaxValue);

        // check if its exception dat
        var exceptionDay = await _context.ExceptionDays
            .Where(e => e.StartAt <= dayEnd && (e.EndsAt == null || e.EndsAt.Value >= dayStart))
            .FirstOrDefaultAsync();

        bool isExceptionDay = exceptionDay != null;

        // get all exception breaks
        var exceptionBreaks = await _context.ExceptionBreaks
            .Where(eb => eb.StartsAt <= dayEnd && (eb.EndsAt == null || eb.EndsAt.Value >= dayStart))
            .ToListAsync();

        var defaultBreaks = await _context.Breaks
            .OrderBy(b => b.BreakNumber ?? b.Id)
            .ToListAsync();

        var breakList = defaultBreaks.Select(b =>
        {
            var startAt = targetDate.ToDateTime(TimeOnly.FromDateTime(b.StartAt));
            var endsAt = b.EndsAt.HasValue
                ? targetDate.ToDateTime(TimeOnly.FromDateTime(b.EndsAt.Value))
                : targetDate.ToDateTime(TimeOnly.FromDateTime(b.StartAt));

            // check if this break is exception break
            var matchingExceptionBreak = exceptionBreaks.FirstOrDefault(eb =>
                eb.StartsAt <= endsAt && (eb.EndsAt == null || eb.EndsAt.Value >= startAt)
            );

            bool isMuted = isExceptionDay || matchingExceptionBreak != null;
            string? muteReason = isExceptionDay 
                ? exceptionDay?.Description 
                : matchingExceptionBreak?.Description;

            return new BreakResponseDto(
                Id: b.Id,
                BreakNumber: b.BreakNumber ?? b.Id,
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

        // check if today is exception day
        var activeExceptionDay = await _context.ExceptionDays
            .Where(e => e.StartAt <= now && (e.EndsAt == null || e.EndsAt.Value >= now))
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

        // check if its exception break
        var activeExceptionBreak = await _context.ExceptionBreaks
            .Where(eb => eb.StartsAt <= now && (eb.EndsAt == null || eb.EndsAt.Value >= now))
            .FirstOrDefaultAsync();

        // todays date
        var today = DateOnly.FromDateTime(now);
        var defaultBreaks = await _context.Breaks
            .OrderBy(b => b.BreakNumber ?? b.Id)
            .ToListAsync();

        var todayBreaks = defaultBreaks.Select(b =>
        {
            var startAt = today.ToDateTime(TimeOnly.FromDateTime(b.StartAt));
            var endsAt = b.EndsAt.HasValue
                ? today.ToDateTime(TimeOnly.FromDateTime(b.EndsAt.Value))
                : today.ToDateTime(TimeOnly.FromDateTime(b.StartAt));

            return new BreakResponseDto(
                Id: b.Id,
                BreakNumber: b.BreakNumber ?? b.Id,
                StartAt: startAt,
                EndsAt: endsAt,
                VoteEnds: b.VoteEnds.HasValue ? today.ToDateTime(TimeOnly.FromDateTime(b.VoteEnds.Value)) : null,
                IsMuted: activeExceptionBreak != null && (activeExceptionBreak.StartsAt <= endsAt && (activeExceptionBreak.EndsAt == null || activeExceptionBreak.EndsAt.Value >= startAt)),
                MuteReason: activeExceptionBreak?.Description
            );
        }).ToList();

        var activeBreak = todayBreaks.FirstOrDefault(b => now >= b.StartAt && now <= b.EndsAt);
        var nextBreak = todayBreaks.Where(b => b.StartAt > now).OrderBy(b => b.StartAt).FirstOrDefault();

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
        else if (activeExceptionBreak != null)
        {
            message = $"Aktualnie trwa wyciszenie radiowęzła ({activeExceptionBreak.Description}).";
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
            .OrderByDescending(e => e.StartAt)
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
        int userId = GetCurrentUserId() ?? 0;
        if (!await IsAdmin(userId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Message = "Tylko administrator może zarządzać dniami wyjątkowymi"
            });
        }

        if (dto.EndsAt.HasValue && dto.EndsAt <= dto.StartAt)
        {
            return BadRequest(new
            {
                Message = "Data zakończenia musi być późniejsza niż data rozpoczęcia"
            });
        }

        var exceptionDay = new ExceptionDay
        {
            StartAt = dto.StartAt,
            EndsAt = dto.EndsAt,
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
        int userId = GetCurrentUserId() ?? 0;
        if (!await IsAdmin(userId))
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
            .OrderByDescending(eb => eb.StartsAt)
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
        int userId = GetCurrentUserId() ?? 0;
        if (!await IsAdmin(userId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                Message = "Tylko administrator może zarządzać przerwami wyjątkowymi"
            });
        }

        if (dto.EndsAt.HasValue && dto.EndsAt <= dto.StartsAt)
        {
            return BadRequest(new
            {
                Message = "Data zakończenia musi być późniejsza niż data rozpoczęcia"
            });
        }

        var exceptionBreak = new ExceptionBreak
        {
            StartsAt = dto.StartsAt,
            EndsAt = dto.EndsAt,
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
        int userId = GetCurrentUserId() ?? 0;
        if (!await IsAdmin(userId))
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
        int userId = GetCurrentUserId() ?? 0;
        if (!await IsAdmin(userId))
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

    private int? GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return int.TryParse(claim, out int id) ? id : null;
    }

    private async Task<bool> IsAdmin(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return false;
        }
        return user.RankId >= 3;
    }
}