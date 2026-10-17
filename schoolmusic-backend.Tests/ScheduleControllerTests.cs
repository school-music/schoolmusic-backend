using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Tests;

public class ScheduleControllerTests
{
    private static schoolmusicContext CreateContext() => new(
        new DbContextOptionsBuilder<schoolmusicContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void SetUser(ControllerBase controller, int? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : [];
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
            }
        };
    }

    private static async Task SeedAdmin(schoolmusicContext db)
    {
        db.Users.Add(new User
        {
            Id = 50, UserLogin = "admin", UserPassword = "hash", RankId = 3,
            Rank = new Rank { Id = 3, Rank1 = "admin" }, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetBreaks_CombinesDefaultBreaksAndExceptionMute()
    {
        await using var db = CreateContext();
        var date = new DateOnly(2026, 10, 19);
        db.Breaks.AddRange(
            new Break { Id = 1, BreakNumber = 1, BreakDate = date, StartAt = new DateTime(2026, 1, 1, 8, 0, 0), EndsAt = new DateTime(2026, 1, 1, 8, 10, 0) },
            new Break { Id = 2, BreakNumber = 2, BreakDate = date, StartAt = new DateTime(2026, 1, 1, 9, 0, 0), EndsAt = new DateTime(2026, 1, 1, 9, 10, 0) });
        db.ExceptionDays.Add(new ExceptionDay { Id = 1, StartDate = date, EndDate = date, DayType = "no_music", Description = "Exam" });
        db.ExceptionBreaks.Add(new ExceptionBreak { Id = 1, BreakDate = date, BreakNumber = 2, MusicEnabled = false, Description = "Assembly" });
        await db.SaveChangesAsync();
        var controller = new RecessController(db);

        var result = Assert.IsType<OkObjectResult>(await controller.GetBreaks(date));
        var response = result.Value!;
        var breaks = (IEnumerable<RecessController.BreakResponseDto>)response.GetType().GetProperty("Przerwy")!.GetValue(response)!;

        Assert.Equal(2, breaks.Count());
        Assert.All(breaks, item => Assert.True(item.IsMuted));
        Assert.Contains(breaks, item => item.BreakNumber == 1 && item.MuteReason == "Exam");
        Assert.Contains(breaks, item => item.BreakNumber == 2 && item.MuteReason == "Exam");
    }

    [Fact]
    public async Task CreateExceptionDay_RejectsEndBeforeStart_AndCreatesValidDay()
    {
        await using var db = CreateContext();
        await SeedAdmin(db);
        var controller = new RecessController(db);
        SetUser(controller, 50);
        var start = new DateOnly(2026, 10, 20);

        Assert.IsType<BadRequestObjectResult>(await controller.CreateExceptionDay(
            new RecessController.CreateExceptionDayDto(start, start.AddDays(-1), "day_off", null)));
        Assert.Empty(db.ExceptionDays);

        Assert.IsType<OkObjectResult>(await controller.CreateExceptionDay(
            new RecessController.CreateExceptionDayDto(start, null, null, "Holiday")));
        var created = Assert.Single(db.ExceptionDays);
        Assert.Equal(start, created.EndDate);
        Assert.Equal("no_music", created.DayType);
        Assert.Equal("Holiday", created.Description);
    }

    [Fact]
    public async Task CreateExceptionBreak_RequiresAdminAndPersistsException()
    {
        await using var db = CreateContext();
        var controller = new RecessController(db);
        SetUser(controller, 1);
        var date = new DateOnly(2026, 10, 21);
        var dto = new RecessController.CreateExceptionBreakDto(date, 3, false, "Event");

        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.CreateExceptionBreak(dto)).StatusCode);
        await SeedAdmin(db);
        SetUser(controller, 50);
        Assert.IsType<OkObjectResult>(await controller.CreateExceptionBreak(dto));
        var saved = Assert.Single(db.ExceptionBreaks);
        Assert.Equal(date, saved.BreakDate);
        Assert.Equal((sbyte)3, saved.BreakNumber);
        Assert.False(saved.MusicEnabled);
    }

    [Fact]
    public async Task UpdateDefaultBreaks_RejectsEmptyList_AndUpdatesExistingBreak()
    {
        await using var db = CreateContext();
        await SeedAdmin(db);
        var controller = new RecessController(db);
        SetUser(controller, 50);

        Assert.IsType<BadRequestObjectResult>(await controller.UpdateDefaultBreaks([]));
        var oldStart = new DateTime(2026, 1, 1, 8, 0, 0);
        var brk = new Break { Id = 8, BreakDate = new DateOnly(2026, 10, 22), BreakNumber = 2, StartAt = oldStart, EndsAt = oldStart.AddMinutes(10) };
        db.Breaks.Add(brk);
        await db.SaveChangesAsync();

        Assert.IsType<OkObjectResult>(await controller.UpdateDefaultBreaks([
            new RecessController.UpdateBreakScheduleItemDto(8, new TimeOnly(9, 5), new TimeOnly(9, 15), new TimeOnly(9, 10))
        ]));
        Assert.Equal(new DateTime(2026, 1, 1, 9, 5, 0), brk.StartAt);
        Assert.Equal(new DateTime(2026, 1, 1, 9, 15, 0), brk.EndsAt);
        Assert.Equal(new DateTime(2026, 1, 1, 9, 10, 0), brk.VoteEnds);
    }

    [Fact]
    public async Task GetWeeklySchedule_ReturnsAllSevenDaysWithDefaultsAndStoredValues()
    {
        await using var db = CreateContext();
        db.WeeklySchedules.Add(new WeeklySchedule { Weekday = 3, MusicEnabled = false });
        await db.SaveChangesAsync();
        var controller = new WeeklyScheduleController(db);

        var result = Assert.IsType<OkObjectResult>(await controller.GetWeeklySchedule());
        var days = Assert.IsAssignableFrom<IEnumerable<WeeklyDayDto>>(result.Value).ToList();

        Assert.Equal(7, days.Count);
        Assert.True(days.Single(day => day.Weekday == 1).MusicEnabled);
        Assert.False(days.Single(day => day.Weekday == 3).MusicEnabled);
        Assert.Equal("Niedziela", days.Single(day => day.Weekday == 7).DayName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public async Task WeeklyDayEndpoints_RejectWeekdaysOutsideOneThroughSeven(int weekday)
    {
        await using var db = CreateContext();
        var controller = new WeeklyScheduleController(db);

        Assert.IsType<BadRequestObjectResult>(await controller.UpdateWeeklyDay(weekday, new UpdateWeeklyDayDto(false)));
        Assert.IsType<BadRequestObjectResult>(await controller.GetWeeklyDayBreaks(weekday));
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateWeeklyBreak(weekday, 1, new UpdateWeeklyBreakDto(false)));
    }

    [Fact]
    public async Task UpdateWeeklyDay_CreatesThenUpdatesStoredRule()
    {
        await using var db = CreateContext();
        var controller = new WeeklyScheduleController(db);

        Assert.IsType<OkObjectResult>(await controller.UpdateWeeklyDay(2, new UpdateWeeklyDayDto(false)));
        Assert.IsType<OkObjectResult>(await controller.UpdateWeeklyDay(2, new UpdateWeeklyDayDto(true)));

        var schedule = Assert.Single(db.WeeklySchedules);
        Assert.Equal((sbyte)2, schedule.Weekday);
        Assert.True(schedule.MusicEnabled);
    }

    [Fact]
    public async Task GetAndUpdateWeeklyBreak_ReflectOverridesAndRejectMissingTemplate()
    {
        await using var db = CreateContext();
        db.BreakTemplates.AddRange(
            new BreakTemplate { Id = 1, BreakNumber = 1, StartsTime = new TimeOnly(8, 0), EndsTime = new TimeOnly(8, 10) },
            new BreakTemplate { Id = 2, BreakNumber = 2, StartsTime = new TimeOnly(9, 0), EndsTime = new TimeOnly(9, 10) });
        db.WeeklySchedules.Add(new WeeklySchedule { Weekday = 1, MusicEnabled = false });
        await db.SaveChangesAsync();
        var controller = new WeeklyScheduleController(db);

        var getResult = Assert.IsType<OkObjectResult>(await controller.GetWeeklyDayBreaks(1));
        var breaks = (IEnumerable<WeeklyBreakDetailDto>)getResult.Value!.GetType().GetProperty("Breaks")!.GetValue(getResult.Value)!;
        Assert.Equal(2, breaks.Count());
        Assert.All(breaks, item => Assert.False(item.MusicEnabled));
        Assert.IsType<NotFoundObjectResult>(await controller.UpdateWeeklyBreak(1, 9, new UpdateWeeklyBreakDto(true)));

        Assert.IsType<OkObjectResult>(await controller.UpdateWeeklyBreak(1, 2, new UpdateWeeklyBreakDto(true)));
        var overrideSetting = Assert.Single(db.WeeklyBreakSettings);
        Assert.Equal((sbyte)2, overrideSetting.BreakNumber);
        Assert.True(overrideSetting.MusicEnabled);
    }
}
