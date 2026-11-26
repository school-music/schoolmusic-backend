using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services;

public sealed record VotingTargetDto(
    int? EligibleBreakId,
    int? BreakId,
    int? BreakNumber,
    DateOnly? BreakDate,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset? CutoffAt,
    string TimeZone,
    DateTimeOffset ServerTime,
    bool IsOpen,
    string Status,
    string ReasonCode,
    string Reason,
    bool AllowVotingOutsideSchedule
);

public sealed record BreakVotingEvaluation(
    Break BreakEntity,
    DateOnly TargetDate,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset CutoffAt,
    bool MusicEnabled,
    bool IsOpen,
    string Status,
    string ReasonCode,
    string Reason
);

/// <summary>
/// Centralne źródło prawdy wyznaczające przerwę kwalifikującą się do głosowania, czas odcięcia ze strefą czasową
/// oraz powód otwarcia/zamknięcia głosowania zgodnie z hierarchią harmonogramu.
/// </summary>
public interface IVotingTargetService
{
    Task<VotingTargetDto> GetVotingTargetAsync(int? requestedBreakId = null, CancellationToken cancellationToken = default);
    Task<BreakVotingEvaluation?> EvaluateBreakAsync(int breakId, CancellationToken cancellationToken = default);
    Task<bool> IsVotingClosedForBreakAsync(Break breakEntity, CancellationToken cancellationToken = default);
}

public sealed class VotingTargetService(
    schoolmusicContext context,
    IConfiguration? configuration = null,
    TimeProvider? timeProvider = null) : IVotingTargetService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<VotingTargetDto> GetVotingTargetAsync(int? requestedBreakId = null, CancellationToken cancellationToken = default)
    {
        var serverNowOffset = _timeProvider.GetLocalNow();
        var now = serverNowOffset.DateTime;
        var today = DateOnly.FromDateTime(now);
        var timeZoneId = _timeProvider.LocalTimeZone.Id;
        bool allowOutside = configuration?.GetValue<bool>("Voting:AllowVotingOutsideSchedule") ?? false;

        if (requestedBreakId.HasValue)
        {
            var requested = await context.Breaks.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == requestedBreakId.Value, cancellationToken);
            if (requested == null)
            {
                return new VotingTargetDto(
                    EligibleBreakId: null,
                    BreakId: null,
                    BreakNumber: null,
                    BreakDate: null,
                    StartsAt: null,
                    EndsAt: null,
                    CutoffAt: null,
                    TimeZone: timeZoneId,
                    ServerTime: serverNowOffset,
                    IsOpen: false,
                    Status: "not_found",
                    ReasonCode: "break_not_found",
                    Reason: "Wybrana przerwa nie istnieje.",
                    AllowVotingOutsideSchedule: allowOutside);
            }

            var eval = await EvaluateBreakInternalAsync(requested, now, today, allowOutside, cancellationToken);
            return new VotingTargetDto(
                EligibleBreakId: eval.IsOpen ? eval.BreakEntity.Id : null,
                BreakId: eval.BreakEntity.Id,
                BreakNumber: eval.BreakEntity.BreakNumber,
                BreakDate: eval.TargetDate,
                StartsAt: eval.StartsAt,
                EndsAt: eval.EndsAt,
                CutoffAt: eval.CutoffAt,
                TimeZone: timeZoneId,
                ServerTime: serverNowOffset,
                IsOpen: eval.IsOpen,
                Status: eval.Status,
                ReasonCode: eval.ReasonCode,
                Reason: eval.Reason,
                AllowVotingOutsideSchedule: allowOutside);
        }

        var exceptionDay = await context.ExceptionDays.AsNoTracking()
            .Where(e => e.StartDate <= today && today <= e.EndDate)
            .FirstOrDefaultAsync(cancellationToken);

        var todayExplicitBreaks = await context.Breaks.AsNoTracking()
            .Where(b => b.BreakDate == today)
            .OrderBy(b => b.BreakNumber)
            .ToListAsync(cancellationToken);

        sbyte weekday = GetIsoWeekday(now);
        bool isWeekend = weekday >= 6;

        if (!allowOutside && exceptionDay != null)
        {
            bool isDayOff = string.Equals(exceptionDay.DayType, "day_off", StringComparison.OrdinalIgnoreCase);
            return new VotingTargetDto(
                EligibleBreakId: null,
                BreakId: null,
                BreakNumber: null,
                BreakDate: today,
                StartsAt: null,
                EndsAt: null,
                CutoffAt: null,
                TimeZone: timeZoneId,
                ServerTime: serverNowOffset,
                IsOpen: false,
                Status: "closed",
                ReasonCode: isDayOff ? "holiday" : "no_music_day",
                Reason: string.IsNullOrWhiteSpace(exceptionDay.Description)
                    ? (isDayOff ? "Dzień wolny od zajęć." : "Dzień bez muzyki.")
                    : exceptionDay.Description!,
                AllowVotingOutsideSchedule: false);
        }

        if (!allowOutside && isWeekend && todayExplicitBreaks.Count == 0)
        {
            return new VotingTargetDto(
                EligibleBreakId: null,
                BreakId: null,
                BreakNumber: null,
                BreakDate: today,
                StartsAt: null,
                EndsAt: null,
                CutoffAt: null,
                TimeZone: timeZoneId,
                ServerTime: serverNowOffset,
                IsOpen: false,
                Status: "closed",
                ReasonCode: "weekend",
                Reason: "Weekend — głosowanie na przerwy szkolne jest zamknięte.",
                AllowVotingOutsideSchedule: false);
        }

        var scheduleBreaks = todayExplicitBreaks.Count > 0
            ? todayExplicitBreaks
            : (await context.Breaks.AsNoTracking()
                .OrderBy(b => b.BreakNumber)
                .ToListAsync(cancellationToken))
                .DistinctBy(b => b.BreakNumber)
                .ToList();

        if (scheduleBreaks.Count == 0)
        {
            return new VotingTargetDto(
                EligibleBreakId: null,
                BreakId: null,
                BreakNumber: null,
                BreakDate: today,
                StartsAt: null,
                EndsAt: null,
                CutoffAt: null,
                TimeZone: timeZoneId,
                ServerTime: serverNowOffset,
                IsOpen: false,
                Status: "closed",
                ReasonCode: "no_breaks_configured",
                Reason: "Brak zdefiniowanych przerw w harmonogramie.",
                AllowVotingOutsideSchedule: allowOutside);
        }

        var evaluations = new List<BreakVotingEvaluation>(scheduleBreaks.Count);
        foreach (var candidate in scheduleBreaks)
        {
            evaluations.Add(await EvaluateBreakInternalAsync(candidate, now, today, allowOutside, cancellationToken));
        }

        if (allowOutside)
        {
            var preferred = evaluations.FirstOrDefault(e => e.MusicEnabled && e.EndsAt.LocalDateTime > now)
                ?? evaluations.FirstOrDefault(e => e.MusicEnabled)
                ?? evaluations[0];

            return new VotingTargetDto(
                EligibleBreakId: preferred.BreakEntity.Id,
                BreakId: preferred.BreakEntity.Id,
                BreakNumber: preferred.BreakEntity.BreakNumber,
                BreakDate: preferred.TargetDate,
                StartsAt: preferred.StartsAt,
                EndsAt: preferred.EndsAt,
                CutoffAt: preferred.CutoffAt,
                TimeZone: timeZoneId,
                ServerTime: serverNowOffset,
                IsOpen: true,
                Status: "open",
                ReasonCode: "schedule_override_enabled",
                Reason: $"Głosowanie na przerwę nr {preferred.BreakEntity.BreakNumber} jest otwarte (tryb poza harmonogramem).",
                AllowVotingOutsideSchedule: true);
        }

        var openUpcoming = evaluations.FirstOrDefault(e => e.IsOpen && e.CutoffAt.LocalDateTime > now);
        if (openUpcoming != null)
        {
            return new VotingTargetDto(
                EligibleBreakId: openUpcoming.BreakEntity.Id,
                BreakId: openUpcoming.BreakEntity.Id,
                BreakNumber: openUpcoming.BreakEntity.BreakNumber,
                BreakDate: openUpcoming.TargetDate,
                StartsAt: openUpcoming.StartsAt,
                EndsAt: openUpcoming.EndsAt,
                CutoffAt: openUpcoming.CutoffAt,
                TimeZone: timeZoneId,
                ServerTime: serverNowOffset,
                IsOpen: true,
                Status: "open",
                ReasonCode: "voting_open",
                Reason: openUpcoming.Reason,
                AllowVotingOutsideSchedule: false);
        }

        var activeClosed = evaluations.FirstOrDefault(e =>
            now >= e.CutoffAt.LocalDateTime && now < e.EndsAt.LocalDateTime);
        if (activeClosed != null)
        {
            return new VotingTargetDto(
                EligibleBreakId: null,
                BreakId: activeClosed.BreakEntity.Id,
                BreakNumber: activeClosed.BreakEntity.BreakNumber,
                BreakDate: activeClosed.TargetDate,
                StartsAt: activeClosed.StartsAt,
                EndsAt: activeClosed.EndsAt,
                CutoffAt: activeClosed.CutoffAt,
                TimeZone: timeZoneId,
                ServerTime: serverNowOffset,
                IsOpen: false,
                Status: "closed",
                ReasonCode: activeClosed.ReasonCode,
                Reason: activeClosed.Reason,
                AllowVotingOutsideSchedule: false);
        }

        bool anyMusicEnabledToday = evaluations.Any(e => e.MusicEnabled);
        var lastBreak = evaluations[^1];
        bool allEnded = now >= lastBreak.EndsAt.LocalDateTime;

        return new VotingTargetDto(
            EligibleBreakId: null,
            BreakId: null,
            BreakNumber: null,
            BreakDate: today,
            StartsAt: null,
            EndsAt: null,
            CutoffAt: allEnded && anyMusicEnabledToday ? lastBreak.CutoffAt : null,
            TimeZone: timeZoneId,
            ServerTime: serverNowOffset,
            IsOpen: false,
            Status: "closed",
            ReasonCode: anyMusicEnabledToday ? "after_hours" : "all_breaks_muted",
            Reason: anyMusicEnabledToday
                ? "Wszystkie dzisiejsze przerwy dobiegły końca."
                : "Muzyka na wszystkich dzisiejszych przerwach jest wyciszona.",
            AllowVotingOutsideSchedule: false);
    }

    public async Task<BreakVotingEvaluation?> EvaluateBreakAsync(int breakId, CancellationToken cancellationToken = default)
    {
        var breakEntity = await context.Breaks.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == breakId, cancellationToken);
        if (breakEntity == null)
            return null;

        var now = _timeProvider.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(now);
        bool allowOutside = configuration?.GetValue<bool>("Voting:AllowVotingOutsideSchedule") ?? false;

        return await EvaluateBreakInternalAsync(breakEntity, now, today, allowOutside, cancellationToken);
    }

    public async Task<bool> IsVotingClosedForBreakAsync(Break breakEntity, CancellationToken cancellationToken = default)
    {
        bool allowOutside = configuration?.GetValue<bool>("Voting:AllowVotingOutsideSchedule") ?? false;
        if (allowOutside)
        {
            return false;
        }

        var now = _timeProvider.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(now);
        var evaluation = await EvaluateBreakInternalAsync(breakEntity, now, today, allowOutside: false, cancellationToken);

        // Zachowujemy możliwość głosowania/zgłaszania na kolejny cykl po zakończeniu przerwy,
        // natomiast blokujemy w oknie od cutoff do EndsAt lub gdy dzień/przerwa jest wyciszona przez wyjątek.
        if (evaluation.ReasonCode is "holiday" or "no_music_day" or "break_muted" or "voting_cutoff_passed")
        {
            return true;
        }

        return false;
    }

    private async Task<BreakVotingEvaluation> EvaluateBreakInternalAsync(
        Break breakEntity,
        DateTime now,
        DateOnly today,
        bool allowOutside,
        CancellationToken cancellationToken)
    {
        var startLocal = today.ToDateTime(TimeOnly.FromDateTime(breakEntity.StartAt));
        var endLocal = today.ToDateTime(TimeOnly.FromDateTime(breakEntity.EndsAt));
        if (endLocal <= startLocal)
        {
            endLocal = endLocal.AddDays(1);
        }

        var cutoffLocal = breakEntity.VoteEnds.HasValue
            ? today.ToDateTime(TimeOnly.FromDateTime(breakEntity.VoteEnds.Value))
            : startLocal;

        var startsAtOffset = ToSchoolOffset(startLocal);
        var endsAtOffset = ToSchoolOffset(endLocal);
        var cutoffOffset = ToSchoolOffset(cutoffLocal);

        var (musicEnabled, muteReason) = await ResolveMusicEnabledAsync(today, GetIsoWeekday(now), breakEntity, cancellationToken);

        if (allowOutside)
        {
            return new BreakVotingEvaluation(
                breakEntity,
                today,
                startsAtOffset,
                endsAtOffset,
                cutoffOffset,
                musicEnabled,
                IsOpen: true,
                Status: "open",
                ReasonCode: "schedule_override_enabled",
                Reason: $"Głosowanie na przerwę nr {breakEntity.BreakNumber} jest otwarte (tryb poza harmonogramem).");
        }

        var exceptionDay = await context.ExceptionDays.AsNoTracking()
            .Where(e => e.StartDate <= today && today <= e.EndDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (exceptionDay != null)
        {
            bool isDayOff = string.Equals(exceptionDay.DayType, "day_off", StringComparison.OrdinalIgnoreCase);
            return new BreakVotingEvaluation(
                breakEntity,
                today,
                startsAtOffset,
                endsAtOffset,
                cutoffOffset,
                MusicEnabled: false,
                IsOpen: false,
                Status: "closed",
                ReasonCode: isDayOff ? "holiday" : "no_music_day",
                Reason: string.IsNullOrWhiteSpace(exceptionDay.Description)
                    ? (isDayOff ? "Dzień wolny od zajęć." : "Dzień bez muzyki.")
                    : exceptionDay.Description!);
        }

        if (!musicEnabled)
        {
            return new BreakVotingEvaluation(
                breakEntity,
                today,
                startsAtOffset,
                endsAtOffset,
                cutoffOffset,
                MusicEnabled: false,
                IsOpen: false,
                Status: "closed",
                ReasonCode: "break_muted",
                Reason: muteReason ?? "Muzyka jest wyłączona dla tej przerwy.");
        }

        if (now >= cutoffLocal && now < endLocal)
        {
            return new BreakVotingEvaluation(
                breakEntity,
                today,
                startsAtOffset,
                endsAtOffset,
                cutoffOffset,
                MusicEnabled: true,
                IsOpen: false,
                Status: "closed",
                ReasonCode: "voting_cutoff_passed",
                Reason: "Głosowanie na tę przerwę dobiegło już końca.");
        }

        if (now >= endLocal)
        {
            return new BreakVotingEvaluation(
                breakEntity,
                today,
                startsAtOffset,
                endsAtOffset,
                cutoffOffset,
                MusicEnabled: true,
                IsOpen: false,
                Status: "closed",
                ReasonCode: "after_hours",
                Reason: $"Przerwa nr {breakEntity.BreakNumber} na dziś dobiegła końca.");
        }

        return new BreakVotingEvaluation(
            breakEntity,
            today,
            startsAtOffset,
            endsAtOffset,
            cutoffOffset,
            MusicEnabled: true,
            IsOpen: true,
            Status: "open",
            ReasonCode: "voting_open",
            Reason: $"Głosowanie na przerwę nr {breakEntity.BreakNumber} jest otwarte do {cutoffLocal:HH:mm}.");
    }

    private async Task<(bool Enabled, string? Reason)> ResolveMusicEnabledAsync(
        DateOnly today,
        sbyte weekday,
        Break breakEntity,
        CancellationToken cancellationToken)
    {
        var exceptionBreak = await context.ExceptionBreaks.AsNoTracking()
            .FirstOrDefaultAsync(eb => eb.BreakDate == today && eb.BreakNumber == breakEntity.BreakNumber, cancellationToken);
        if (exceptionBreak != null)
        {
            return (exceptionBreak.MusicEnabled, exceptionBreak.MusicEnabled ? null : exceptionBreak.Description ?? "Wyjątek kalendarzowy.");
        }

        var weeklyBreak = await context.WeeklyBreakSettings.AsNoTracking()
            .FirstOrDefaultAsync(wb => wb.Weekday == weekday && wb.BreakNumber == breakEntity.BreakNumber, cancellationToken);
        if (weeklyBreak != null)
        {
            return (weeklyBreak.MusicEnabled, weeklyBreak.MusicEnabled ? null : "Muzyka wyłączona w harmonogramie tygodniowym dla tej przerwy.");
        }

        var weeklyDay = await context.WeeklySchedules.AsNoTracking()
            .FirstOrDefaultAsync(ws => ws.Weekday == weekday, cancellationToken);
        if (weeklyDay != null)
        {
            return (weeklyDay.MusicEnabled, weeklyDay.MusicEnabled ? null : "Muzyka wyłączona w harmonogramie tygodniowym dla tego dnia.");
        }

        return (breakEntity.MusicEnabled, breakEntity.MusicEnabled ? null : "Muzyka wyłączona w konfiguracji przerwy.");
    }

    private DateTimeOffset ToSchoolOffset(DateTime localWallTime)
    {
        var tz = _timeProvider.LocalTimeZone;
        var unspecified = DateTime.SpecifyKind(localWallTime, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        var offset = tz.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset);
    }

    private static sbyte GetIsoWeekday(DateTime dateTime)
    {
        sbyte weekday = (sbyte)dateTime.DayOfWeek;
        return weekday == 0 ? (sbyte)7 : weekday;
    }
}
