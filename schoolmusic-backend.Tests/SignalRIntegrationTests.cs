using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using schoolmusic_backend.Hubs;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Tests;

public class SignalRIntegrationTests
{
    [Fact]
    public async Task KioskGroup_ReceivesSchoolAndNowPlayingEvents_OnlyAfterJoining()
    {
        using var host = await CreateHostAsync();
        var server = host.GetTestServer();
        await using var kiosk = CreateConnection(server);
        await using var otherClient = CreateConnection(server);
        var schoolStatus = new TaskCompletionSource<SystemStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nowPlaying = new TaskCompletionSource<PlayerStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherClientReceivedEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        kiosk.On<SystemStatusDto>("SchoolStatusChanged", status => schoolStatus.TrySetResult(status));
        kiosk.On<PlayerStatusDto>("NowPlayingChanged", status => nowPlaying.TrySetResult(status));
        otherClient.On<SystemStatusDto>("SchoolStatusChanged", _ => otherClientReceivedEvent.TrySetResult());
        otherClient.On<PlayerStatusDto>("NowPlayingChanged", _ => otherClientReceivedEvent.TrySetResult());

        await Task.WhenAll(kiosk.StartAsync(), otherClient.StartAsync());
        await kiosk.InvokeAsync("JoinKiosk");

        var status = new SystemStatusDto(
            SchoolState.Break,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5),
            null,
            300,
            1,
            null,
            true,
            null,
            "Break time",
            null,
            Array.Empty<TopTrackDto>());
        var playerStatus = new PlayerStatusDto(true, false, 50, false, "Url", "Song", "Artist", 0, 180000);
        var hub = server.Services.GetRequiredService<IHubContext<QueueHub>>();

        await hub.Clients.Group(QueueHub.KioskGroup).SendAsync("SchoolStatusChanged", status);
        await hub.Clients.Group(QueueHub.KioskGroup).SendAsync("NowPlayingChanged", playerStatus);

        var receivedSchoolStatus = await schoolStatus.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(status.CurrentState, receivedSchoolStatus.CurrentState);
        Assert.Equal(status.CurrentBreakNumber, receivedSchoolStatus.CurrentBreakNumber);
        Assert.Equal(status.Message, receivedSchoolStatus.Message);

        var receivedPlayerStatus = await nowPlaying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(playerStatus.Title, receivedPlayerStatus.Title);
        Assert.Equal(playerStatus.Artist, receivedPlayerStatus.Artist);
        Assert.Equal(playerStatus.IsPlaying, receivedPlayerStatus.IsPlaying);

        await Task.Delay(100);
        Assert.False(otherClientReceivedEvent.Task.IsCompleted);
    }

    private static Task<IHost> CreateHostAsync() => new HostBuilder()
        .ConfigureWebHost(webHost => webHost
            .UseTestServer()
            .ConfigureServices(services => services.AddSignalR())
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapHub<QueueHub>("/hubs/queue"));
            }))
        .StartAsync();

    private static HubConnection CreateConnection(TestServer server) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/queue"), options =>
            {
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
                options.WebSocketFactory = async (context, cancellationToken) =>
                    await server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken);
            })
            .Build();
}
