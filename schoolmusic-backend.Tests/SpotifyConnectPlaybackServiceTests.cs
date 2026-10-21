using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Tests;

public sealed class SpotifyConnectPlaybackServiceTests
{
    private const string AccountScopes = "user-read-private user-read-playback-state user-modify-playback-state";
    private const string TokenKey = "Spotify:TokenEncryptionKey";

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            return await respond(request);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ServiceProvider Provider { get; }
        public IConfiguration Configuration { get; }

        public Fixture(string? databaseName = null)
        {
            var values = new Dictionary<string, string?>
            {
                ["Spotify:ClientId"] = "client-id",
                ["Spotify:ClientSecret"] = "deployment-secret",
                ["Spotify:RedirectUri"] = "https://api.example.test/api/spotify-playback/callback",
                ["Spotify:DeviceName"] = "PA Device",
                [TokenKey] = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray())
            };
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            var dbName = databaseName ?? Guid.NewGuid().ToString();
            services.AddDbContext<schoolmusicContext>(options => options.UseInMemoryDatabase(dbName));
            Provider = services.BuildServiceProvider();
        }

        public SpotifyConnectPlaybackService CreateService(RecordingHandler handler) =>
            new(
                Provider.GetRequiredService<IServiceScopeFactory>(),
                new StaticClientFactory(new HttpClient(handler)),
                Configuration,
                NullLogger<SpotifyConnectPlaybackService>.Instance);

        public async Task<(SpotifyConnectPlaybackService Service, string RefreshCiphertext, DateTime ExpiresAt, string State)> AuthorizeAsync()
        {
            var handler = new RecordingHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/token")
                    return Task.FromResult(Json(HttpStatusCode.OK, TokenResponse("access-initial", "refresh-initial", 15552000)));
                if (request.RequestUri.AbsolutePath == "/v1/me")
                    return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"school-account\"}"));
                throw new InvalidOperationException($"Unexpected authorization request: {request.RequestUri}");
            });
            var service = CreateService(handler);
            var authorizationUrl = await service.CreateAuthorizationUrlAsync();
            var state = QueryValue(new Uri(authorizationUrl).Query, "state");
            await service.CompleteAuthorizationAsync("authorization-code", state, null);
            using var scope = Provider.CreateScope();
            var connection = await scope.ServiceProvider.GetRequiredService<schoolmusicContext>()
                .SpotifyPlaybackConnections.SingleAsync();
            return (service, connection.RefreshTokenCiphertext, connection.RefreshTokenExpiresAt, state);
        }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
    }

    private sealed class StaticClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [Fact]
    public async Task OAuthStateRejectsUnissuedValues()
    {
        await using var fixture = new Fixture();
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("invalid state must not call Spotify"));
        var service = fixture.CreateService(handler);
        var url = await service.CreateAuthorizationUrlAsync();

        var invalid = await Assert.ThrowsAsync<SpotifyPlaybackException>(() =>
            service.CompleteAuthorizationAsync("code", "not-the-issued-state", null));
        Assert.Equal(400, invalid.StatusCode);
        Assert.Empty(handler.Requests);

        Assert.NotEmpty(QueryValue(new Uri(url).Query, "state"));
    }

    [Fact]
    public async Task AuthorizationPersistsOnlyEncryptedRefreshTokenAndAccountIdentity()
    {
        await using var fixture = new Fixture();
        var (service, ciphertext, expiry, state) = await fixture.AuthorizeAsync();

        using var scope = fixture.Provider.CreateScope();
        var connection = await scope.ServiceProvider.GetRequiredService<schoolmusicContext>()
            .SpotifyPlaybackConnections.SingleAsync();
        Assert.Equal(1, connection.Id);
        Assert.Equal("school-account", connection.SpotifyUserId);
        Assert.NotEqual("refresh-initial", ciphertext);
        Assert.Equal("refresh-initial", Decrypt(ciphertext, fixture.Configuration[TokenKey]!));
        Assert.Equal(expiry, connection.RefreshTokenExpiresAt);
        Assert.True(connection.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.True(connection.UpdatedAt > DateTime.UtcNow.AddMinutes(-1));
        var replay = await Assert.ThrowsAsync<SpotifyPlaybackException>(() =>
            service.CompleteAuthorizationAsync("authorization-code", state, null));
        Assert.Equal(400, replay.StatusCode);
    }

    [Fact]
    public async Task RefreshPersistsReplacementTokenWithoutExtendingOriginalLifetime()
    {
        await using var fixture = new Fixture();
        var (_, initialCiphertext, originalExpiry, _) = await fixture.AuthorizeAsync();
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/token")
                return Task.FromResult(Json(HttpStatusCode.OK, TokenResponse("access-refreshed", "refresh-replacement", null)));
            if (request.RequestUri.AbsolutePath == "/v1/me/player/devices")
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"devices\":[]}"));
            throw new InvalidOperationException($"Unexpected refresh request: {request.RequestUri}");
        });
        var service = fixture.CreateService(handler);

        var state = await service.GetPlaybackStateAsync();

        Assert.False(state.DeviceAvailable);
        var tokenRequest = Assert.Single(handler.Requests, request => request.Path == "/api/token");
        Assert.Contains("refresh_token=refresh-initial", tokenRequest.Body);
        using var scope = fixture.Provider.CreateScope();
        var connection = await scope.ServiceProvider.GetRequiredService<schoolmusicContext>()
            .SpotifyPlaybackConnections.SingleAsync();
        Assert.NotEqual(initialCiphertext, connection.RefreshTokenCiphertext);
        Assert.Equal("refresh-replacement", Decrypt(connection.RefreshTokenCiphertext, fixture.Configuration[TokenKey]!));
        Assert.Equal(originalExpiry, connection.RefreshTokenExpiresAt);
    }

    [Fact]
    public async Task PlaybackDiscoversConfiguredDeviceAndSendsTrackUrisInOrder()
    {
        await using var fixture = new Fixture();
        await fixture.AuthorizeAsync();
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/api/token")
                return Task.FromResult(Json(HttpStatusCode.OK, TokenResponse("access-playback", null, null)));
            if (path == "/v1/me/player/devices")
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"devices\":[{\"id\":\"living-room\",\"name\":\"Living Room\",\"is_active\":true,\"is_restricted\":false,\"volume_percent\":35},{\"id\":\"pa-runtime-9\",\"name\":\"PA Device\",\"is_active\":false,\"is_restricted\":false,\"volume_percent\":60}]}"));
            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/tracks/", StringComparison.Ordinal))
            {
                var id = path["/v1/tracks/".Length..].Split('?', 2)[0];
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $"{{\"id\":\"{id}\",\"uri\":\"spotify:track:{id}\",\"is_playable\":true}}"));
            }
            if (request.Method == HttpMethod.Put && path == "/v1/me/player/play?device_id=pa-runtime-9")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            throw new InvalidOperationException($"Unexpected playback request: {request.Method} {path}");
        });
        var service = fixture.CreateService(handler);
        IReadOnlyList<SpotifyQueueTrack> queue =
        [
            new(12, "A12345678901234567890", "First", "Artist 1"),
            new(13, "B12345678901234567890", "Second", "Artist 2")
        ];

        await service.StartPlaybackAsync(queue);

        Assert.Single(handler.Requests, request => request.Path == "/v1/me/player/devices");
        var play = Assert.Single(handler.Requests, request => request.Path == "/v1/me/player/play?device_id=pa-runtime-9");
        using var body = System.Text.Json.JsonDocument.Parse(play.Body!);
        var uris = body.RootElement.GetProperty("uris").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(new[]
        {
            "spotify:track:A12345678901234567890",
            "spotify:track:B12345678901234567890"
        }, uris);
    }

    [Fact]
    public async Task PlaybackSkipsUnplayableTracksAndUsesRelinkedTrackUrisInOrder()
    {
        await using var fixture = new Fixture();
        await fixture.AuthorizeAsync();
        var firstId = "A12345678901234567890";
        var unavailableId = "B12345678901234567890";
        var relinkedId = "C12345678901234567890";
        var alternateId = "D12345678901234567890";
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/api/token")
                return Task.FromResult(Json(HttpStatusCode.OK, TokenResponse("access-playback", null, null)));
            if (path == "/v1/me/player/devices")
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"devices\":[{\"id\":\"pa-runtime-9\",\"name\":\"PA Device\",\"is_active\":false,\"is_restricted\":false,\"volume_percent\":60}]}"));
            if (path == $"/v1/tracks/{firstId}?market=from_token")
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $"{{\"id\":\"{firstId}\",\"uri\":\"spotify:track:{firstId}\",\"is_playable\":true}}"));
            if (path == $"/v1/tracks/{unavailableId}?market=from_token")
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $"{{\"id\":\"{unavailableId}\",\"uri\":\"spotify:track:{unavailableId}\",\"is_playable\":false}}"));
            if (path == $"/v1/tracks/{relinkedId}?market=from_token")
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $"{{\"id\":\"{alternateId}\",\"uri\":\"spotify:track:{alternateId}\",\"is_playable\":true,\"linked_from\":{{\"id\":\"{relinkedId}\"}}}}"));
            if (request.Method == HttpMethod.Put && path == "/v1/me/player/play?device_id=pa-runtime-9")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            throw new InvalidOperationException($"Unexpected playback request: {request.Method} {path}");
        });
        var service = fixture.CreateService(handler);
        IReadOnlyList<SpotifyQueueTrack> queue =
        [
            new(21, firstId, "First", "Artist 1"),
            new(22, unavailableId, "Unavailable", "Artist 2"),
            new(23, relinkedId, "Relinked", "Artist 3")
        ];

        var accepted = await service.StartPlaybackAsync(queue);

        Assert.Equal(new[] { 21, 23 }, accepted.Select(track => track.QueueItemId));
        Assert.Equal(new[] { firstId, alternateId }, accepted.Select(track => track.SpotifyId));
        var play = Assert.Single(handler.Requests, request => request.Path == "/v1/me/player/play?device_id=pa-runtime-9");
        using var body = System.Text.Json.JsonDocument.Parse(play.Body!);
        var uris = body.RootElement.GetProperty("uris").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(new[] { $"spotify:track:{firstId}", $"spotify:track:{alternateId}" }, uris);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string TokenResponse(string access, string? refresh, int? refreshLifetime)
    {
        var replacement = refresh == null ? "" : $",\"refresh_token\":\"{refresh}\"";
        var lifetime = refreshLifetime.HasValue ? $",\"refresh_token_expires_in\":{refreshLifetime.Value}" : "";
        return $"{{\"access_token\":\"{access}\"{replacement},\"expires_in\":3600,\"scope\":\"{AccountScopes}\"{lifetime}}}";
    }

    private static string QueryValue(string query, string key) =>
        query.TrimStart('?').Split('&')
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Where(parts => Uri.UnescapeDataString(parts[0]) == key)
            .Select(parts => Uri.UnescapeDataString(parts[1]))
            .Single();

    private static string Decrypt(string ciphertext, string base64Key)
    {
        var bytes = Convert.FromBase64String(ciphertext);
        var plaintext = new byte[bytes.Length - 28];
        using var aes = new AesGcm(Convert.FromBase64String(base64Key), 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
