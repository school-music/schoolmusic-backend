using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using schoolmusic_backend.Configuration;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Tests;

public sealed class AudioAndSpotifyTests
{
    [Fact]
    public void BuildVlcOptions_UsesPlatformDefaultsAndExplicitConfigWithoutHardcodedDirectSoundOnLinux()
    {
        var linuxDefault = AudioPlayerService.BuildVlcOptions(new AudioOptions(), isWindows: false);
        Assert.DoesNotContain(linuxDefault, o => o.Contains("directsound", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(linuxDefault, o => o.StartsWith("--aout=", StringComparison.Ordinal));

        var windowsDefault = AudioPlayerService.BuildVlcOptions(new AudioOptions(), isWindows: true);
        Assert.Contains("--aout=directsound", windowsDefault);

        var dockerDummy = AudioPlayerService.BuildVlcOptions(
            new AudioOptions
            {
                LibVlcOutput = "dummy",
                ExtraVlcOptions = ["--no-video", " --verbose=1 ", ""]
            },
            isWindows: false);
        Assert.Contains("--aout=dummy", dockerDummy);
        Assert.Contains("--no-video", dockerDummy);
        Assert.Contains("--verbose=1", dockerDummy);
        Assert.DoesNotContain("--aout=directsound", dockerDummy);

        var linuxAlsa = AudioPlayerService.BuildVlcOptions(
            new AudioOptions { LibVlcOutput = "alsa" },
            isWindows: true);
        Assert.Contains("--aout=alsa", linuxAlsa);
        Assert.DoesNotContain("--aout=directsound", linuxAlsa);
    }

    [Fact]
    public async Task SpotifyConnectAndCallbackEndpoints_EnforceRolesRedirectAndMapCallbackErrorsToProblemDetails()
    {
        var spotifyMock = new Mock<ISpotifyConnectPlaybackService>();
        var schedulerMock = new Mock<IBreakSchedulerService>();

        spotifyMock.Setup(s => s.CreateAuthorizationUrlAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://accounts.spotify.com/authorize?state=test-state");
        spotifyMock.Setup(s => s.CompleteAuthorizationAsync("valid-code", "test-state", null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        spotifyMock.Setup(s => s.CompleteAuthorizationAsync(It.IsAny<string?>(), "bad-state", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SpotifyPlaybackException(400, "Invalid or expired Spotify OAuth state."));
        spotifyMock.Setup(s => s.CompleteAuthorizationAsync(null, "test-state", "access_denied", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SpotifyPlaybackException(403, "Spotify authorization was denied."));
        spotifyMock.Setup(s => s.GetConnectionStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpotifyConnectionStatus(true, true, "radio-account", "pa-device", true, false, null, "Connected"));

        using var baseFactory = new TestWebApplicationFactory();
        await baseFactory.SeedUserAsync("radio_admin", "AdminPass123!", rankId: 3);
        await baseFactory.SeedUserAsync("radio_student", "StudentPass123!", rankId: 1);

        var customFactory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISpotifyConnectPlaybackService>();
                services.AddSingleton(spotifyMock.Object);
                services.RemoveAll<IBreakSchedulerService>();
                services.AddSingleton(schedulerMock.Object);
            });
        });

        using var anonNoRedirectClient = customFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var adminNoRedirectClient = customFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var studentClient = customFactory.CreateClient();

        var adminLogin = await (await anonNoRedirectClient.PostAsJsonAsync("/api/auth/login", new
        {
            name = "radio_admin",
            password = "AdminPass123!"
        })).Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        adminNoRedirectClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminLogin!.jwtSecurityToken);

        var studentLogin = await (await studentClient.PostAsJsonAsync("/api/auth/login", new
        {
            name = "radio_student",
            password = "StudentPass123!"
        })).Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        studentClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", studentLogin!.jwtSecurityToken);

        // 1. Connect: bez tokena -> 401; uczeń -> 403; admin -> 302 bez JSON body
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonNoRedirectClient.GetAsync("/api/spotify-playback/connect")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await studentClient.GetAsync("/api/spotify-playback/connect")).StatusCode);

        var connectResp = await adminNoRedirectClient.GetAsync("/api/spotify-playback/connect");
        Assert.Equal(HttpStatusCode.Found, connectResp.StatusCode);
        Assert.Equal("https://accounts.spotify.com/authorize?state=test-state", connectResp.Headers.Location?.ToString());

        // 2. Callback anonymous: błędny state -> 400 ProblemDetails; odrzucona zgoda -> 403 ProblemDetails
        var badState = await anonNoRedirectClient.GetAsync("/api/spotify-playback/callback?code=c1&state=bad-state");
        Assert.Equal(HttpStatusCode.BadRequest, badState.StatusCode);
        Assert.Equal("application/problem+json", badState.Content.Headers.ContentType?.MediaType);
        var badProblem = await badState.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("state", badProblem!.Detail, StringComparison.OrdinalIgnoreCase);

        var deniedResp = await anonNoRedirectClient.GetAsync("/api/spotify-playback/callback?state=test-state&error=access_denied");
        Assert.Equal(HttpStatusCode.Forbidden, deniedResp.StatusCode);
        Assert.Equal("application/problem+json", deniedResp.Content.Headers.ContentType?.MediaType);

        // 3. Callback sukces: text/plain 200 oraz powiadomienie schedulera
        var okCallback = await anonNoRedirectClient.GetAsync("/api/spotify-playback/callback?code=valid-code&state=test-state");
        Assert.Equal(HttpStatusCode.OK, okCallback.StatusCode);
        Assert.Equal("text/plain", okCallback.Content.Headers.ContentType?.MediaType);
        schedulerMock.Verify(s => s.NotifyPlaybackConfigurationChanged(), Times.Once);

        // 4. Status endpoint działa dla admina
        var statusResp = await adminNoRedirectClient.GetAsync("/api/spotify-playback/status");
        statusResp.EnsureSuccessStatusCode();
        var status = await statusResp.Content.ReadFromJsonAsync<SpotifyConnectionStatus>();
        Assert.True(status!.Configured);
    }
}
