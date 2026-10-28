using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Tests;

public class BreakTemplatesControllerTests
{
    private static (BreakTemplatesController Controller, schoolmusicContext Context) CreateController()
    {
        var context = new schoolmusicContext(new DbContextOptionsBuilder<schoolmusicContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        return (new BreakTemplatesController(context), context);
    }

    [Fact]
    public async Task GetBreakTemplates_ReturnsTemplatesInBreakNumberOrder()
    {
        var (controller, context) = CreateController();
        context.BreakTemplates.AddRange(
            Template(2, 4),
            Template(1, 1));
        await context.SaveChangesAsync();

        var result = await controller.GetBreakTemplates();
        var response = Assert.IsType<OkObjectResult>(result.Result);
        var templates = Assert.IsAssignableFrom<IReadOnlyList<BreakTemplateDto>>(response.Value);

        Assert.Equal(new[] { 1, 4 }, templates.Select(template => template.BreakNumber));
    }

    [Fact]
    public async Task CreateBreakTemplate_ValidRequest_CreatesTemplate()
    {
        var (controller, context) = CreateController();

        var result = await controller.CreateBreakTemplate(
            new CreateBreakTemplateDto(9, new TimeOnly(12, 0), new TimeOnly(12, 15)));

        var created = Assert.IsType<CreatedResult>(result);
        var dto = Assert.IsType<BreakTemplateDto>(created.Value);
        Assert.Equal(9, dto.BreakNumber);
        Assert.Equal(new TimeOnly(12, 0), dto.StartsTime);
        Assert.Equal(1, await context.BreakTemplates.CountAsync());
    }

    [Fact]
    public async Task CreateBreakTemplate_NotifiesSchedulerAndInvalidatesScheduleCache()
    {
        await using var context = new schoolmusicContext(new DbContextOptionsBuilder<schoolmusicContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var scheduler = new Mock<IBreakSchedulerService>();
        var statusService = new Mock<ISystemStatusService>();
        var controller = new BreakTemplatesController(context, scheduler.Object, statusService.Object);

        await controller.CreateBreakTemplate(
            new CreateBreakTemplateDto(9, new TimeOnly(12, 0), new TimeOnly(12, 15)));

        scheduler.Verify(service => service.NotifyScheduleChanged(), Times.Once);
        statusService.Verify(service => service.InvalidateScheduleCache(), Times.Once);
    }

    [Theory]
    [InlineData(0, 7, 45, 7, 55)]
    [InlineData(128, 7, 45, 7, 55)]
    [InlineData(1, 8, 0, 7, 55)]
    [InlineData(1, 7, 55, 7, 55)]
    public async Task CreateBreakTemplate_InvalidNumberOrTimes_ReturnsBadRequest(
        int number, int startHour, int startMinute, int endHour, int endMinute)
    {
        var (controller, _) = CreateController();

        var result = await controller.CreateBreakTemplate(new CreateBreakTemplateDto(
            number,
            new TimeOnly(startHour, startMinute),
            new TimeOnly(endHour, endMinute)));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateBreakTemplate_DuplicateNumber_ReturnsConflict()
    {
        var (controller, context) = CreateController();
        context.BreakTemplates.Add(Template(1, 1));
        await context.SaveChangesAsync();

        var result = await controller.CreateBreakTemplate(
            new CreateBreakTemplateDto(1, new TimeOnly(12, 0), new TimeOnly(12, 15)));

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task UpdateBreakTemplate_UpdatesFields()
    {
        var (controller, context) = CreateController();
        context.BreakTemplates.Add(Template(1, 2));
        await context.SaveChangesAsync();

        var result = await controller.UpdateBreakTemplate(
            1,
            new UpdateBreakTemplateDto(3, new TimeOnly(9, 0), new TimeOnly(9, 20)));

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<BreakTemplateDto>(ok.Value);
        Assert.Equal(3, dto.BreakNumber);
        Assert.Equal(new TimeOnly(9, 20), dto.EndsTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateBreakTemplate_ChangingReferencedNumber_ReturnsConflict(bool weeklyReference)
    {
        var (controller, context) = CreateController();
        context.BreakTemplates.Add(Template(1, 2));
        if (weeklyReference)
        {
            context.WeeklyBreakSettings.Add(new WeeklyBreakSetting
            {
                Weekday = 1,
                BreakNumber = 2,
                MusicEnabled = true
            });
        }
        else
        {
            context.Breaks.Add(new Break
            {
                Id = 1,
                BreakDate = new DateOnly(2026, 1, 5),
                BreakNumber = 2,
                StartAt = new DateTime(2026, 1, 5, 8, 0, 0),
                EndsAt = new DateTime(2026, 1, 5, 8, 15, 0)
            });
        }
        await context.SaveChangesAsync();

        var result = await controller.UpdateBreakTemplate(
            1,
            new UpdateBreakTemplateDto(3, new TimeOnly(9, 0), new TimeOnly(9, 20)));

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(2, (await context.BreakTemplates.FindAsync(1))!.BreakNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteBreakTemplate_WhenReferenced_ReturnsConflict(bool weeklyReference)
    {
        var (controller, context) = CreateController();
        context.BreakTemplates.Add(Template(1, 2));
        if (weeklyReference)
        {
            context.WeeklyBreakSettings.Add(new WeeklyBreakSetting
            {
                Weekday = 1,
                BreakNumber = 2,
                MusicEnabled = true
            });
        }
        else
        {
            context.Breaks.Add(new Break
            {
                Id = 1,
                BreakDate = new DateOnly(2026, 1, 5),
                BreakNumber = 2,
                StartAt = new DateTime(2026, 1, 5, 8, 0, 0),
                EndsAt = new DateTime(2026, 1, 5, 8, 15, 0)
            });
        }
        await context.SaveChangesAsync();

        var result = await controller.DeleteBreakTemplate(1);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.NotNull(await context.BreakTemplates.FindAsync(1));
    }

    [Fact]
    public async Task DeleteBreakTemplate_WhenUnreferenced_ReturnsNoContent()
    {
        var (controller, context) = CreateController();
        context.BreakTemplates.Add(Template(1, 2));
        await context.SaveChangesAsync();

        var result = await controller.DeleteBreakTemplate(1);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await context.BreakTemplates.FindAsync(1));
    }

    [Fact]
    public async Task DeleteBreakTemplate_WhenMissing_ReturnsNotFound()
    {
        var (controller, _) = CreateController();

        Assert.IsType<NotFoundObjectResult>(await controller.DeleteBreakTemplate(404));
    }

    [Fact]
    public void Controller_RequiresAdminOrHeadAdminRole()
    {
        var authorize = typeof(BreakTemplatesController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Single();

        Assert.Equal("admin,head_admin", authorize.Roles);
    }

    private static BreakTemplate Template(int id, sbyte number) => new()
    {
        Id = id,
        BreakNumber = number,
        StartsTime = new TimeOnly(8, 0),
        EndsTime = new TimeOnly(8, 15)
    };
}
