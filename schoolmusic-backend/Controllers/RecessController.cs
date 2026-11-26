using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

[Route("api/breaks")]
[ApiController]
[Authorize]
[Produces("application/json")]
public class RecessController : ControllerBase
{
    private readonly schoolmusicContext _context;
    private readonly IBreakSchedulerService? _scheduler;
    private readonly ISystemStatusService? _statusService;
    private readonly TimeProvider _timeProvider;

    public RecessController(
        schoolmusicContext context,
        IBreakSchedulerService? scheduler = null,
        ISystemStatusService? statusService = null,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _scheduler = scheduler;
        _statusService = statusService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private void NotifyScheduleUpdated()
    {
        _statusService?.InvalidateScheduleCache();
        _scheduler?.NotifyScheduleChanged();
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
    public record CreateExceptionDayDto(DateOnly StartDate, DateOnly? EndDate, string? DayType, string? Description);
    public record CreateExceptionBreakDto(DateOnly BreakDate, sbyte BreakNumber, bool MusicEnabled = false, string? Description = null);

    public sealed record ExceptionBreakItemDto(
        int Id,
        DateOnly BreakDate,
        sbyte BreakNumber,
        bool MusicEnabled,
        string? Description
    );

    public sealed record ExceptionDayItemDto(
        int Id,
        DateOnly StartDate,
        DateOnly EndDate,
        string DayType,
        string? Description
    );

    public sealed record BreaksScheduleResponseDto(
        DateOnly Date,
        bool IsExceptionDay,
        bool IsMusicEnabled,
        string? ExceptionDescription,
        string Message,
        IReadOnlyList<BreakResponseDto> Przerwy,
        IReadOnlyList<ExceptionBreakItemDto> WyjatkowePrzerwy
    );

    public sealed record CurrentBreakStatusResponseDto(
        DateTime CurrentTime,
        bool IsExceptionDay,
        bool IsExceptionBreak,
        bool IsBreakActive,
        bool IsMusicEnabled,
        string? MuteReason,
        BreakResponseDto? ActiveBreak,
        BreakResponseDto? NextBreak,
        string Message
    );

    public sealed record ExceptionDaysListResponseDto(
        string Message,
        IReadOnlyList<ExceptionDayItemDto> Exceptions
    );

    public sealed record ExceptionDayMutationResponseDto(
        string Message,
        ExceptionDayItemDto ExceptionDay
    );

    public sealed record ExceptionBreaksListResponseDto(
        string Message,
        IReadOnlyList<ExceptionBreakItemDto> ExceptionBreaks
    );

    public sealed record ExceptionBreakMutationResponseDto(
        string Message,
        ExceptionBreakItemDto ExceptionBreak
    );

    public sealed record BreakMessageResponseDto(string Message);

    /// <summary>
    /// Pobiera harmonogram przerw na dany dzień z informacją
    /// czy obowiązuje ExceptionDay lub ExceptionBreak.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<BreaksScheduleResponseDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBreaks([FromQuery] DateOnly? date)
    {
        var targetDate = date ?? DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);

        var exceptionDay = await _context.ExceptionDays
            .Where(e => e.StartDate <= targetDate && targetDate <= e.EndDate)
            .FirstOrDefaultAsync();

        bool isExceptionDay = exceptionDay != null;

        var exceptionBreaks = await _context.ExceptionBreaks
            .Where(eb => eb.BreakDate == targetDate)
            .ToListAsync();

        var defaultBreaks = await _context.Breaks
            .Where(b => b.BreakDate == targetDate)
            .OrderBy(b => b.BreakNumber)
            .ToListAsync();

        if (defaultBreaks.Count == 0)
        {
            defaultBreaks = await _context.Breaks
                .OrderBy(b => b.BreakNumber)
                .Take(8)
                .ToListAsync();
        }

        var breakList = defaultBreaks.Select(b =>
        {
            var startAt = targetDate.ToDateTime(TimeOnly.FromDateTime(b.StartAt));
            var endsAt = targetDate.ToDateTime(TimeOnly.FromDateTime(b.EndsAt));

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

        var exceptionBreakDtos = exceptionBreaks
            .Select(eb => new ExceptionBreakItemDto(eb.Id, eb.BreakDate, eb.BreakNumber, eb.MusicEnabled, eb.Description))
            .ToList();

        return Ok(new BreaksScheduleResponseDto(
            Date: targetDate,
            IsExceptionDay: isExceptionDay,
            IsMusicEnabled: !isExceptionDay && breakList.Any(b => !b.IsMuted),
            ExceptionDescription: exceptionDay?.Description,
            Message: isExceptionDay
                ? $"W dniu {targetDate:yyyy-MM-dd} obowiązuje dzień bez muzyki ({exceptionDay?.Description})."
                : $"Plan przerw na dzień {targetDate:yyyy-MM-dd}.",
            Przerwy: breakList,
            WyjatkowePrzerwy: exceptionBreakDtos
        ));
    }

    /// <summary>
    /// Sprawdza bieżący status: czy trwa przerwa,
    /// czy muzyka ma grać, czy obowiązuje ExceptionDay lub ExceptionBreak.
    /// </summary>
    [HttpGet("current-status")]
    [AllowAnonymous]
    [ProducesResponseType<CurrentBreakStatusResponseDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurrentStatus()
    {
        var now = _timeProvider.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(now);

        var activeExceptionDay = await _context.ExceptionDays
            .Where(e => e.StartDate <= today && today <= e.EndDate)
            .FirstOrDefaultAsync();

        if (activeExceptionDay != null)
        {
            return Ok(new CurrentBreakStatusResponseDto(
                CurrentTime: now,
                IsExceptionDay: true,
                IsExceptionBreak: false,
                IsBreakActive: false,
                IsMusicEnabled: false,
                MuteReason: activeExceptionDay.Description,
                ActiveBreak: null,
                NextBreak: null,
                Message: $"Aktualnie trwa dzień wyjątkowy: {activeExceptionDay.Description}. Muzyka nie gra."
            ));
        }

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

        return Ok(new CurrentBreakStatusResponseDto(
            CurrentTime: now,
            IsExceptionDay: false,
            IsExceptionBreak: activeExceptionBreak != null,
            IsBreakActive: activeBreak != null,
            IsMusicEnabled: isMusicPlaying,
            MuteReason: activeExceptionBreak?.Description,
            ActiveBreak: activeBreak,
            NextBreak: nextBreak,
            Message: message
        ));
    }

    /// <summary>
    /// Pobiera listę wszystkich zaplanowanych dni wyjątkowych (cały dzień bez muzyki).
    /// </summary>
    [HttpGet("exceptions")]
    [ProducesResponseType<ExceptionDaysListResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAllExceptionDays()
    {
        var exceptions = await _context.ExceptionDays
            .OrderByDescending(e => e.StartDate)
            .Select(e => new ExceptionDayItemDto(e.Id, e.StartDate, e.EndDate, e.DayType, e.Description))
            .ToListAsync();

        return Ok(new ExceptionDaysListResponseDto("Lista dni wyjątkowych", exceptions));
    }

    /// <summary>
    /// Dodaje dzień wyjątkowy. Tylko dla administratora.
    /// </summary>
    [HttpPost("exceptions")]
    [ProducesResponseType<ExceptionDayMutationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateExceptionDay([FromBody] CreateExceptionDayDto dto)
    {
        int userId = HttpContext.GetCurrentUserId() ?? 0;
        if (!await _context.IsAdminAsync(userId))
        {
            return this.Create(StatusCodes.Status403Forbidden, "Tylko administrator może zarządzać dniami wyjątkowymi");
        }

        var endDate = dto.EndDate ?? dto.StartDate;
        if (endDate < dto.StartDate)
        {
            return this.Create(StatusCodes.Status400BadRequest, "Data zakończenia musi być późniejsza lub równa dacie rozpoczęcia");
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
        NotifyScheduleUpdated();

        return Ok(new ExceptionDayMutationResponseDto(
            "Pomyślnie dodano dzień wyjątkowy",
            new ExceptionDayItemDto(exceptionDay.Id, exceptionDay.StartDate, exceptionDay.EndDate, exceptionDay.DayType, exceptionDay.Description)));
    }

    /// <summary>
    /// Usuwa dzień wyjątkowy o podanym identyfikatorze. Tylko dla administratora.
    /// </summary>
    [HttpDelete("exceptions/{id:int}")]
    [ProducesResponseType<BreakMessageResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteExceptionDay(int id)
    {
        int userId = HttpContext.GetCurrentUserId() ?? 0;
        if (!await _context.IsAdminAsync(userId))
        {
            return this.Create(StatusCodes.Status403Forbidden, "Tylko administrator może usuwać dni wyjątkowe");
        }

        var exceptionDay = await _context.ExceptionDays.FindAsync(id);
        if (exceptionDay == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono dnia wyjątkowego o podanym ID");
        }

        _context.ExceptionDays.Remove(exceptionDay);
        await _context.SaveChangesAsync();
        NotifyScheduleUpdated();

        return Ok(new BreakMessageResponseDto("Pomyślnie usunięto dzień wyjątkowy"));
    }

    /// <summary>
    /// Pobiera listę zaplanowanych wyciszeń konkretnych przerw (ExceptionBreak).
    /// </summary>
    [HttpGet("exception-breaks")]
    [ProducesResponseType<ExceptionBreaksListResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAllExceptionBreaks()
    {
        var exceptionBreaks = await _context.ExceptionBreaks
            .OrderByDescending(eb => eb.BreakDate)
            .ThenBy(eb => eb.BreakNumber)
            .Select(eb => new ExceptionBreakItemDto(eb.Id, eb.BreakDate, eb.BreakNumber, eb.MusicEnabled, eb.Description))
            .ToListAsync();

        return Ok(new ExceptionBreaksListResponseDto("Lista wyciszonych przerw (wyjątków)", exceptionBreaks));
    }

    /// <summary>
    /// Dodaje wyciszenie konkretnej przerwy. Tylko dla administratora.
    /// </summary>
    [HttpPost("exception-breaks")]
    [ProducesResponseType<ExceptionBreakMutationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateExceptionBreak([FromBody] CreateExceptionBreakDto dto)
    {
        int userId = HttpContext.GetCurrentUserId() ?? 0;
        if (!await _context.IsAdminAsync(userId))
        {
            return this.Create(StatusCodes.Status403Forbidden, "Tylko administrator może zarządzać przerwami wyjątkowymi");
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
        NotifyScheduleUpdated();

        return Ok(new ExceptionBreakMutationResponseDto(
            "Pomyślnie dodano przerwę z wyciszeniem muzyki",
            new ExceptionBreakItemDto(exceptionBreak.Id, exceptionBreak.BreakDate, exceptionBreak.BreakNumber, exceptionBreak.MusicEnabled, exceptionBreak.Description)));
    }

    /// <summary>
    /// Usuwa wyciszenie konkretnej przerwy. Tylko dla administratora.
    /// </summary>
    [HttpDelete("exception-breaks/{id:int}")]
    [ProducesResponseType<BreakMessageResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteExceptionBreak(int id)
    {
        int userId = HttpContext.GetCurrentUserId() ?? 0;
        if (!await _context.IsAdminAsync(userId))
        {
            return this.Create(StatusCodes.Status403Forbidden, "Tylko administrator może usuwać wyjątki przerw");
        }

        var exceptionBreak = await _context.ExceptionBreaks.FindAsync(id);
        if (exceptionBreak == null)
        {
            return this.Create(StatusCodes.Status404NotFound, "Nie znaleziono wyjątku przerwy o podanym ID");
        }

        _context.ExceptionBreaks.Remove(exceptionBreak);
        await _context.SaveChangesAsync();
        NotifyScheduleUpdated();

        return Ok(new BreakMessageResponseDto("Pomyślnie usunięto wyjątek przerwy"));
    }

    /// <summary>
    /// Pozwala administratorowi na edycję stałych godzin przerw w tabeli break.
    /// </summary>
    [HttpPut("update-break")]
    [ProducesResponseType<BreakMessageResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateDefaultBreaks([FromBody] List<UpdateBreakScheduleItemDto> items)
    {
        int userId = HttpContext.GetCurrentUserId() ?? 0;
        if (!await _context.IsAdminAsync(userId))
        {
            return this.Create(StatusCodes.Status403Forbidden, "Tylko administrator może edytować rozkład przerw");
        }

        if (items == null || items.Count == 0)
        {
            return this.Create(StatusCodes.Status400BadRequest, "Lista przerw nie może być pusta");
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
        NotifyScheduleUpdated();

        return Ok(new BreakMessageResponseDto("Rozkład przerw w bazie został zaktualizowany"));
    }
}
