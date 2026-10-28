using schoolmusic_backend.Services;

namespace schoolmusic_backend.Tests;

public sealed class SchoolTimeProviderTests
{
    [Fact]
    public void GetLocalNow_DuringSpringClockChange_SkipsNonexistentHour()
    {
        var source = new ManualTimeProvider(new DateTime(2026, 3, 29, 0, 30, 0));
        var provider = new SchoolTimeProvider(source, SchoolTimeProvider.ResolveTimeZone("Europe/Warsaw"));

        var beforeChange = provider.GetLocalNow();
        source.Advance(TimeSpan.FromHours(1));
        var afterChange = provider.GetLocalNow();

        Assert.Equal(1, beforeChange.Hour);
        Assert.Equal(TimeSpan.FromHours(1), beforeChange.Offset);
        Assert.Equal(3, afterChange.Hour);
        Assert.Equal(TimeSpan.FromHours(2), afterChange.Offset);
    }

    [Fact]
    public void GetLocalNow_DuringAutumnClockChange_RepresentsRepeatedHourWithDifferentOffset()
    {
        var source = new ManualTimeProvider(new DateTime(2026, 10, 25, 0, 30, 0));
        var provider = new SchoolTimeProvider(source, SchoolTimeProvider.ResolveTimeZone("Europe/Warsaw"));

        var summerOccurrence = provider.GetLocalNow();
        source.Advance(TimeSpan.FromHours(1));
        var winterOccurrence = provider.GetLocalNow();

        Assert.Equal(2, summerOccurrence.Hour);
        Assert.Equal(2, winterOccurrence.Hour);
        Assert.Equal(TimeSpan.FromHours(2), summerOccurrence.Offset);
        Assert.Equal(TimeSpan.FromHours(1), winterOccurrence.Offset);
    }

    [Fact]
    public void ResolveTimeZone_WhenIdentifierIsInvalid_ThrowsConfigurationError()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SchoolTimeProvider.ResolveTimeZone("Invalid/SchoolTimeZone"));
    }
}
