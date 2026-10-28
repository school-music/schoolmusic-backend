using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers
{
    public record UpdateWeeklyDayDto(bool MusicEnabled);
    public record UpdateWeeklyBreakDto(bool MusicEnabled);

    public record WeeklyDayDto(sbyte Weekday, string DayName, bool MusicEnabled);

    public record WeeklyBreakDetailDto(
        sbyte BreakNumber,
        string StartsTime,
        string EndsTime,
        bool MusicEnabled,
        bool IsOverridden
    );

    public record CalendarSpecialEventDto(
        int Id,
        string? Title,
        string FileUrl,
        DateTime StartsAt,
        string EventStatus
    );

    public record CalendarBreakDto(
        sbyte BreakNumber,
        string StartsTime,
        string EndsTime,
        DateTime StartAt,
        DateTime EndsAt,
        bool IsMusicEnabled,
        bool IsOverridden,
        string RuleSource,
        string? Reason
    );

    public record CalendarExceptionDayDto(
        int Id,
        DateOnly StartDate,
        DateOnly EndDate,
        string DayType,
        string? Description
    );

    public record CalendarDayDto(
        DateOnly Date,
        sbyte Weekday,
        string DayName,
        string DayType,
        string StatusDescription,
        bool IsSchoolDay,
        bool IsMusicEnabled,
        bool HasExceptions,
        CalendarExceptionDayDto? ExceptionDay,
        string BellScheduleTemplate,
        List<CalendarBreakDto> Breaks,
        List<CalendarSpecialEventDto> SoundEvents
    );

    public record CalendarScheduleResponseDto(
        DateOnly From,
        DateOnly To,
        int TotalDays,
        int SchoolDaysCount,
        int DaysOffCount,
        int MusicActiveDaysCount,
        List<CalendarDayDto> Days
    );

    /// <summary>
    /// Kontroler do zarządzania domyślnym harmonogramem tygodniowym przerw i odtwarzania muzyki
    /// </summary>
    [Route("api/schedule")]
    [ApiController]
    public class WeeklyScheduleController : ControllerBase
    {
        private readonly schoolmusicContext _context;
        private readonly IBreakSchedulerService? _scheduler;
        private readonly ISystemStatusService? _statusService;

        private static readonly Dictionary<sbyte, string> DayNames = new()
        {
            { 1, "Poniedziałek" },
            { 2, "Wtorek" },
            { 3, "Środa" },
            { 4, "Czwartek" },
            { 5, "Piątek" },
            { 6, "Sobota" },
            { 7, "Niedziela" }
        };

        public WeeklyScheduleController(
            schoolmusicContext context,
            IBreakSchedulerService? scheduler = null,
            ISystemStatusService? statusService = null)
        {
            _context = context;
            _scheduler = scheduler;
            _statusService = statusService;
        }

        private void NotifyScheduleUpdated()
        {
            _statusService?.InvalidateScheduleCache();
            _scheduler?.NotifyScheduleChanged();
        }

        /// <summary>
        /// Zwraca konfigurację muzyki dla wszystkich 7 dni tygodnia
        /// </summary>
        [HttpGet("weekly")]
        [AllowAnonymous]
        public async Task<IActionResult> GetWeeklySchedule()
        {
            var schedules = await _context.WeeklySchedules
                .OrderBy(s => s.Weekday)
                .ToListAsync();

            var result = new List<WeeklyDayDto>();
            for (sbyte day = 1; day <= 7; day++)
            {
                var existing = schedules.FirstOrDefault(s => s.Weekday == day);
                string dayName = DayNames.GetValueOrDefault(day, $"Dzień {day}");
                result.Add(new WeeklyDayDto(day, dayName, existing?.MusicEnabled ?? true));
            }

            return Ok(result);
        }

        /// <summary>
        /// Zmienia flagę włączenia muzyki dla całego danego dnia tygodnia
        /// </summary>
        /// <param name="weekday">Dzień tygodnia (1 = Poniedziałek, 7 = Niedziela)</param>
        /// <param name="dto">Nowy stan włączenia muzyki</param>
        [HttpPut("weekly/{weekday:int}")]
        [Authorize(Roles = "admin,head_admin")]
        public async Task<IActionResult> UpdateWeeklyDay(int weekday, [FromBody] UpdateWeeklyDayDto dto)
        {
            if (weekday < 1 || weekday > 7)
            {
                return BadRequest(new { Message = "Numer dnia tygodnia musi być w przedziale 1–7 (1 = Poniedziałek, 7 = Niedziela)." });
            }

            sbyte day = (sbyte)weekday;
            var schedule = await _context.WeeklySchedules.FindAsync(day);
            if (schedule == null)
            {
                schedule = new WeeklySchedule
                {
                    Weekday = day,
                    MusicEnabled = dto.MusicEnabled
                };
                _context.WeeklySchedules.Add(schedule);
            }
            else
            {
                schedule.MusicEnabled = dto.MusicEnabled;
            }

            await _context.SaveChangesAsync();
            NotifyScheduleUpdated();
            return Ok(new
            {
                Message = $"Zaktualizowano konfigurację dla dnia {DayNames.GetValueOrDefault(day, day.ToString())}.",
                Weekday = day,
                dto.MusicEnabled
            });
        }

        /// <summary>
        /// Pobiera listę przerw z szablonu dla danego dnia tygodnia wraz ze statusem włączenia muzyki
        /// </summary>
        [HttpGet("weekly/{weekday:int}/breaks")]
        [AllowAnonymous]
        public async Task<IActionResult> GetWeeklyDayBreaks(int weekday)
        {
            if (weekday < 1 || weekday > 7)
            {
                return BadRequest(new { Message = "Numer dnia tygodnia musi być w przedziale 1–7." });
            }

            sbyte day = (sbyte)weekday;
            var daySchedule = await _context.WeeklySchedules.FindAsync(day);
            bool defaultDayEnabled = daySchedule?.MusicEnabled ?? true;

            var templates = await _context.BreakTemplates
                .OrderBy(t => t.BreakNumber)
                .ToListAsync();

            var overrides = await _context.WeeklyBreakSettings
                .Where(w => w.Weekday == day)
                .ToListAsync();

            var result = templates.Select(t =>
            {
                var customSetting = overrides.FirstOrDefault(o => o.BreakNumber == t.BreakNumber);
                bool isOverridden = customSetting != null;
                bool isMusicEnabled = isOverridden ? customSetting!.MusicEnabled : defaultDayEnabled;

                return new WeeklyBreakDetailDto(
                    BreakNumber: t.BreakNumber,
                    StartsTime: t.StartsTime.ToString("HH:mm:ss"),
                    EndsTime: t.EndsTime.ToString("HH:mm:ss"),
                    MusicEnabled: isMusicEnabled,
                    IsOverridden: isOverridden
                );
            });

            return Ok(new
            {
                Weekday = day,
                DayName = DayNames.GetValueOrDefault(day, day.ToString()),
                DayMusicEnabled = defaultDayEnabled,
                Breaks = result
            });
        }

        /// <summary>
        /// Zmienia flagę włączenia muzyki dla konkretnej przerwy w danym dniu tygodnia
        /// </summary>
        [HttpPut("weekly/{weekday:int}/breaks/{breakNo:int}")]
        [Authorize(Roles = "admin,head_admin")]
        public async Task<IActionResult> UpdateWeeklyBreak(int weekday, int breakNo, [FromBody] UpdateWeeklyBreakDto dto)
        {
            if (weekday < 1 || weekday > 7)
            {
                return BadRequest(new { Message = "Numer dnia tygodnia musi być w przedziale 1–7." });
            }

            sbyte day = (sbyte)weekday;
            sbyte breakNumber = (sbyte)breakNo;

            var templateExists = await _context.BreakTemplates.AnyAsync(t => t.BreakNumber == breakNumber);
            if (!templateExists)
            {
                return NotFound(new { Message = $"Szablon przerwy nr {breakNo} nie istnieje." });
            }

            var setting = await _context.WeeklyBreakSettings
                .FirstOrDefaultAsync(w => w.Weekday == day && w.BreakNumber == breakNumber);

            if (setting == null)
            {
                setting = new WeeklyBreakSetting
                {
                    Weekday = day,
                    BreakNumber = breakNumber,
                    MusicEnabled = dto.MusicEnabled
                };
                _context.WeeklyBreakSettings.Add(setting);
            }
            else
            {
                setting.MusicEnabled = dto.MusicEnabled;
            }

            await _context.SaveChangesAsync();
            NotifyScheduleUpdated();
            return Ok(new
            {
                Message = $"Zaktualizowano przerwę #{breakNo} w dniu {DayNames.GetValueOrDefault(day, day.ToString())}.",
                Weekday = day,
                BreakNumber = breakNo,
                dto.MusicEnabled
            });
        }

        /// <summary>
        /// Zwraca zagregowany widok kalendarza dla zadanego przedziału dat (maks. 31 dni).
        /// Uwzględnia standardowe szablony dzwonków, reguły tygodniowe, dni wolne, wyjątki oraz wydarzenia dźwiękowe.
        /// </summary>
        [HttpGet("calendar")]
        [AllowAnonymous]
        public async Task<IActionResult> GetCalendarSchedule(
            [FromQuery] DateOnly? from = null,
            [FromQuery] DateOnly? to = null)
        {
            var startDate = from ?? DateOnly.FromDateTime(DateTime.Today);
            var endDate = to ?? (from.HasValue ? from.Value.AddDays(30) : startDate.AddDays(30));

            if (startDate > endDate)
            {
                return BadRequest(new { Message = "Data początkowa ('from') nie może być późniejsza niż data końcowa ('to')." });
            }

            int totalDays = endDate.DayNumber - startDate.DayNumber + 1;
            if (totalDays > 31)
            {
                return BadRequest(new { Message = "Maksymalny dozwolony zakres kalendarza wynosi 31 dni." });
            }

            var exceptionDays = await _context.ExceptionDays
                .Where(e => e.StartDate <= endDate && e.EndDate >= startDate)
                .ToListAsync();

            var exceptionBreaks = await _context.ExceptionBreaks
                .Where(eb => eb.BreakDate >= startDate && eb.BreakDate <= endDate)
                .ToListAsync();

            var weeklyDays = await _context.WeeklySchedules.ToListAsync();
            var weeklyBreaks = await _context.WeeklyBreakSettings.ToListAsync();

            var templates = await _context.BreakTemplates
                .OrderBy(t => t.BreakNumber)
                .ToListAsync();

            var startDateTime = startDate.ToDateTime(TimeOnly.MinValue);
            var endDateTime = endDate.ToDateTime(TimeOnly.MaxValue);
            var specialEvents = await _context.SpecialEvents
                .Where(s => s.StartsAt >= startDateTime && s.StartsAt <= endDateTime)
                .OrderBy(s => s.StartsAt)
                .ToListAsync();

            var daysList = new List<CalendarDayDto>();
            int schoolDaysCount = 0;
            int daysOffCount = 0;
            int musicActiveDaysCount = 0;

            for (var curr = startDate; curr <= endDate; curr = curr.AddDays(1))
            {
                sbyte weekday = (sbyte)curr.DayOfWeek;
                if (weekday == 0) weekday = 7;
                string dayName = DayNames.GetValueOrDefault(weekday, $"Dzień {weekday}");

                var excDay = exceptionDays.FirstOrDefault(e => e.StartDate <= curr && curr <= e.EndDate);

                string dayType;
                string statusDescription;
                bool isSchoolDay;
                bool hasExceptions = false;
                CalendarExceptionDayDto? excDayDto = null;

                if (excDay != null)
                {
                    hasExceptions = true;
                    excDayDto = new CalendarExceptionDayDto(
                        excDay.Id,
                        excDay.StartDate,
                        excDay.EndDate,
                        excDay.DayType,
                        excDay.Description
                    );

                    if (excDay.DayType == "day_off")
                    {
                        dayType = "day_off";
                        statusDescription = string.IsNullOrWhiteSpace(excDay.Description) ? "Dzień wolny" : excDay.Description;
                        isSchoolDay = false;
                        daysOffCount++;
                    }
                    else
                    {
                        dayType = "no_music";
                        statusDescription = string.IsNullOrWhiteSpace(excDay.Description) ? "Dzień bez muzyki" : excDay.Description;
                        isSchoolDay = true;
                        schoolDaysCount++;
                    }
                }
                else if (weekday >= 6)
                {
                    dayType = "weekend";
                    statusDescription = "Weekend";
                    isSchoolDay = false;
                    daysOffCount++;
                }
                else
                {
                    var weeklySchedule = weeklyDays.FirstOrDefault(ws => ws.Weekday == weekday);
                    bool weeklyMusic = weeklySchedule?.MusicEnabled ?? true;

                    if (weeklyMusic)
                    {
                        dayType = "school_day";
                        statusDescription = "Dzień nauki";
                    }
                    else
                    {
                        dayType = "school_day_no_music";
                        statusDescription = "Dzień nauki (muzyka wyłączona w harmonogramie tygodniowym)";
                    }
                    isSchoolDay = true;
                    schoolDaysCount++;
                }

                var breaksList = new List<CalendarBreakDto>();
                bool dayHasAnyMusicPlaying = false;

                foreach (var template in templates)
                {
                    var startAt = curr.ToDateTime(template.StartsTime);
                    var endsAt = curr.ToDateTime(template.EndsTime);

                    bool isBreakMusicEnabled;
                    bool isOverridden;
                    string ruleSource;
                    string? reason = null;

                    if (excDay != null)
                    {
                        isBreakMusicEnabled = false;
                        isOverridden = true;
                        ruleSource = "exception_day";
                        reason = excDay.Description ?? (excDay.DayType == "day_off" ? "Dzień wolny" : "Dzień bez muzyki");
                    }
                    else if (weekday >= 6)
                    {
                        isBreakMusicEnabled = false;
                        isOverridden = false;
                        ruleSource = "weekend";
                        reason = "Weekend";
                    }
                    else
                    {
                        var excBreak = exceptionBreaks.FirstOrDefault(eb => eb.BreakDate == curr && eb.BreakNumber == template.BreakNumber);
                        if (excBreak != null)
                        {
                            hasExceptions = true;
                            isBreakMusicEnabled = excBreak.MusicEnabled;
                            isOverridden = true;
                            ruleSource = "exception_break";
                            reason = excBreak.Description ?? (excBreak.MusicEnabled ? "Włączona przez wyjątek przerwy" : "Wyciszona przez wyjątek przerwy");
                        }
                        else
                        {
                            var weeklyBreak = weeklyBreaks.FirstOrDefault(wb => wb.Weekday == weekday && wb.BreakNumber == template.BreakNumber);
                            if (weeklyBreak != null)
                            {
                                isBreakMusicEnabled = weeklyBreak.MusicEnabled;
                                isOverridden = true;
                                ruleSource = "weekly_break";
                                reason = weeklyBreak.MusicEnabled ? "Włączona w harmonogramie tygodniowym dla tej przerwy" : "Wyciszona w harmonogramie tygodniowym dla tej przerwy";
                            }
                            else
                            {
                                var weeklySchedule = weeklyDays.FirstOrDefault(ws => ws.Weekday == weekday);
                                if (weeklySchedule != null)
                                {
                                    isBreakMusicEnabled = weeklySchedule.MusicEnabled;
                                    isOverridden = false;
                                    ruleSource = "weekly_day";
                                    reason = weeklySchedule.MusicEnabled ? null : "Wyciszona w harmonogramie tygodniowym dla tego dnia";
                                }
                                else
                                {
                                    isBreakMusicEnabled = true;
                                    isOverridden = false;
                                    ruleSource = "default";
                                    reason = null;
                                }
                            }
                        }
                    }

                    if (isBreakMusicEnabled)
                    {
                        dayHasAnyMusicPlaying = true;
                    }

                    breaksList.Add(new CalendarBreakDto(
                        BreakNumber: template.BreakNumber,
                        StartsTime: template.StartsTime.ToString("HH:mm:ss"),
                        EndsTime: template.EndsTime.ToString("HH:mm:ss"),
                        StartAt: startAt,
                        EndsAt: endsAt,
                        IsMusicEnabled: isBreakMusicEnabled,
                        IsOverridden: isOverridden,
                        RuleSource: ruleSource,
                        Reason: reason
                    ));
                }

                if (dayHasAnyMusicPlaying)
                {
                    musicActiveDaysCount++;
                }

                var dayEvents = specialEvents
                    .Where(s => DateOnly.FromDateTime(s.StartsAt) == curr)
                    .Select(s => new CalendarSpecialEventDto(
                        Id: s.Id,
                        Title: s.Title,
                        FileUrl: s.FileUrl,
                        StartsAt: s.StartsAt,
                        EventStatus: s.EventStatus
                    ))
                    .ToList();

                string bellTemplateName = templates.Count > 0
                    ? $"Standardowy rozkład dzwonków ({templates.Count} przerw)"
                    : "Rozkład podstawowy";

                daysList.Add(new CalendarDayDto(
                    Date: curr,
                    Weekday: weekday,
                    DayName: dayName,
                    DayType: dayType,
                    StatusDescription: statusDescription,
                    IsSchoolDay: isSchoolDay,
                    IsMusicEnabled: dayHasAnyMusicPlaying,
                    HasExceptions: hasExceptions,
                    ExceptionDay: excDayDto,
                    BellScheduleTemplate: bellTemplateName,
                    Breaks: breaksList,
                    SoundEvents: dayEvents
                ));
            }

            var response = new CalendarScheduleResponseDto(
                From: startDate,
                To: endDate,
                TotalDays: totalDays,
                SchoolDaysCount: schoolDaysCount,
                DaysOffCount: daysOffCount,
                MusicActiveDaysCount: musicActiveDaysCount,
                Days: daysList
            );

            return Ok(response);
        }
    }
}
