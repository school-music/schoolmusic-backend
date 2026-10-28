using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services;

public sealed record SpotifyQueueTrack(int QueueItemId, string SpotifyId, string Title, string Artist);

public sealed record SpotifyPlaybackSnapshot(
    bool DeviceAvailable,
    bool IsPlaying,
    bool IsPaused,
    string? SpotifyTrackId,
    string? Title,
    string? Artist,
    int Volume,
    long ProgressMs,
    long DurationMs,
    string? DeviceName);

public sealed record SpotifyConnectionStatus(
    bool Configured,
    bool Connected,
    string? SpotifyUserId,
    string DeviceName,
    bool DeviceAvailable,
    bool NeedsReauthorization,
    DateTime? RefreshTokenExpiresAtUtc,
    string? Message);

public interface ISpotifyConnectPlaybackService
{
    Task<string> CreateAuthorizationUrlAsync(CancellationToken cancellationToken = default);
    Task CompleteAuthorizationAsync(string? code, string? state, string? error, CancellationToken cancellationToken = default);
    Task<SpotifyConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken = default);
    Task<PlayerStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<SpotifyPlaybackSnapshot> GetPlaybackStateAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SpotifyQueueTrack>> StartPlaybackAsync(IReadOnlyList<SpotifyQueueTrack> tracks, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task SkipToNextAsync(CancellationToken cancellationToken = default);
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default);
}

public sealed class SpotifyPlaybackException(int statusCode, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class SpotifyConnectPlaybackService : ISpotifyConnectPlaybackService
{
    private const string AccountsBaseUrl = "https://accounts.spotify.com";
    private const string ApiBaseUrl = "https://api.spotify.com/v1";
    private const string RequiredScopes = "user-read-private user-read-playback-state user-modify-playback-state";
    private static readonly TimeSpan OAuthStateLifetime = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SpotifyConnectPlaybackService> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingStates = new();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AccessTokenCache? _accessToken;

    public SpotifyConnectPlaybackService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SpotifyConnectPlaybackService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public Task<string> CreateAuthorizationUrlAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOAuthConfiguration();
        var now = DateTimeOffset.UtcNow;
        foreach (var pending in _pendingStates)
        {
            if (now - pending.Value > OAuthStateLifetime)
                _pendingStates.TryRemove(pending.Key, out _);
        }

        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _pendingStates[state] = now;

        var values = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _configuration["Spotify:ClientId"]!,
            ["redirect_uri"] = _configuration["Spotify:RedirectUri"]!,
            ["scope"] = RequiredScopes,
            ["state"] = state,
            ["show_dialog"] = "true"
        };
        var query = string.Join("&", values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return Task.FromResult($"{AccountsBaseUrl}/authorize?{query}");
    }

    public async Task CompleteAuthorizationAsync(
        string? code,
        string? state,
        string? error,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state) ||
            !_pendingStates.TryRemove(state, out var createdAt) ||
            DateTimeOffset.UtcNow - createdAt > OAuthStateLifetime)
        {
            throw new SpotifyPlaybackException(StatusCodes.Status400BadRequest, "Spotify authorization state is invalid or expired.");
        }

        if (!string.IsNullOrWhiteSpace(error))
            throw new SpotifyPlaybackException(StatusCodes.Status400BadRequest, $"Spotify authorization failed: {error}.");
        if (string.IsNullOrWhiteSpace(code))
            throw new SpotifyPlaybackException(StatusCodes.Status400BadRequest, "Spotify did not return an authorization code.");

        EnsureOAuthConfiguration();
        using var response = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _configuration["Spotify:RedirectUri"]!
        }, cancellationToken);
        var token = await ParseTokenResponseAsync(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(token.RefreshToken) || string.IsNullOrWhiteSpace(token.AccessToken))
            throw new SpotifyPlaybackException(StatusCodes.Status502BadGateway, "Spotify did not return the required tokens.");

        var grantedScopes = (token.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var missingScope = RequiredScopes.Split(' ').FirstOrDefault(scope => !grantedScopes.Contains(scope, StringComparer.Ordinal));
        if (missingScope != null)
            throw new SpotifyPlaybackException(StatusCodes.Status403Forbidden, $"Spotify authorization is missing the required scope '{missingScope}'.");

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/me");
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var profileResponse = await _httpClientFactory.CreateClient("Spotify").SendAsync(profileRequest, cancellationToken);
        using var profileJson = await ReadSuccessfulJsonAsync(profileResponse, cancellationToken);
        var spotifyUserId = StringValue(profileJson.RootElement, "id");
        if (string.IsNullOrWhiteSpace(spotifyUserId))
            throw new SpotifyPlaybackException(StatusCodes.Status502BadGateway, "Spotify did not return an account ID.");

        var now = DateTime.UtcNow;
        var refreshExpiresAt = token.RefreshTokenExpiresIn is > 0
            ? now.AddSeconds(token.RefreshTokenExpiresIn.Value)
            : now.AddMonths(6);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        var connection = await db.SpotifyPlaybackConnections.SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        if (connection == null)
        {
            connection = new SpotifyPlaybackConnection { Id = 1, CreatedAt = now };
            db.SpotifyPlaybackConnections.Add(connection);
        }

        connection.SpotifyUserId = spotifyUserId;
        connection.RefreshTokenCiphertext = EncryptRefreshToken(token.RefreshToken);
        connection.RefreshTokenExpiresAt = refreshExpiresAt;
        connection.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        _accessToken = new AccessTokenCache(token.AccessToken, now.AddSeconds(token.ExpiresIn));
    }

    public async Task<SpotifyConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken = default)
    {
        var configured = HasPlaybackConfiguration();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
        var connection = await db.SpotifyPlaybackConnections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        var deviceName = _configuration["Spotify:DeviceName"] ?? "";
        if (connection == null)
        {
            return new SpotifyConnectionStatus(configured, false, null, deviceName, false, false, null,
                configured ? "Spotify playback account has not been connected." : "Spotify playback configuration is incomplete.");
        }

        var needsReauthorization = connection.RefreshTokenExpiresAt <= DateTime.UtcNow;
        if (!configured)
        {
            return new SpotifyConnectionStatus(false, true, connection.SpotifyUserId, deviceName, false,
                needsReauthorization, connection.RefreshTokenExpiresAt, "Spotify playback configuration is incomplete.");
        }
        if (needsReauthorization)
        {
            return new SpotifyConnectionStatus(true, true, connection.SpotifyUserId, deviceName, false, true,
                connection.RefreshTokenExpiresAt, "Spotify authorization has expired; reconnect the account.");
        }

        try
        {
            var device = await FindConfiguredDeviceAsync(cancellationToken);
            return new SpotifyConnectionStatus(true, true, connection.SpotifyUserId, deviceName, device != null,
                false, connection.RefreshTokenExpiresAt, device == null ? "Configured Spotify Connect device is unavailable." : null);
        }
        catch (SpotifyPlaybackException ex)
        {
            var reauth = ex.StatusCode == StatusCodes.Status401Unauthorized;
            return new SpotifyConnectionStatus(true, true, connection.SpotifyUserId, deviceName, false,
                reauth, connection.RefreshTokenExpiresAt, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new SpotifyConnectionStatus(true, true, connection.SpotifyUserId, deviceName, false,
                false, connection.RefreshTokenExpiresAt, "Spotify is temporarily unreachable.");
        }
    }

    public async Task<PlayerStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetPlaybackStateAsync(cancellationToken);
        var hasTrack = !string.IsNullOrWhiteSpace(snapshot.SpotifyTrackId);
        return new PlayerStatusDto(
            snapshot.IsPlaying,
            snapshot.IsPaused,
            snapshot.Volume,
            snapshot.Volume == 0,
            hasTrack ? "SpotifyConnect" : "None",
            snapshot.Title,
            snapshot.Artist,
            snapshot.ProgressMs,
            snapshot.DurationMs);
    }

    public async Task<SpotifyPlaybackSnapshot> GetPlaybackStateAsync(CancellationToken cancellationToken = default)
    {
        await GetAccessTokenAsync(cancellationToken);
        var target = await FindConfiguredDeviceAsync(cancellationToken);
        if (target == null)
            return new SpotifyPlaybackSnapshot(false, false, false, null, null, null, 0, 0, 0, _configuration["Spotify:DeviceName"]);

        using var response = await SendApiAsync(HttpMethod.Get, "/me/player", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return new SpotifyPlaybackSnapshot(true, false, false, null, null, null, target.Volume, 0, 0, target.Name);
        using var json = await ReadSuccessfulJsonAsync(response, cancellationToken);
        var root = json.RootElement;
        var currentDeviceId = root.TryGetProperty("device", out var currentDevice)
            ? StringValue(currentDevice, "id")
            : null;
        if (!string.Equals(currentDeviceId, target.Id, StringComparison.Ordinal))
            return new SpotifyPlaybackSnapshot(true, false, false, null, null, null, target.Volume, 0, 0, target.Name);

        var isPlaying = root.TryGetProperty("is_playing", out var playing) && playing.GetBoolean();
        var progress = root.TryGetProperty("progress_ms", out var progressValue) && progressValue.ValueKind == JsonValueKind.Number
            ? progressValue.GetInt64() : 0;
        var item = root.TryGetProperty("item", out var currentItem) && currentItem.ValueKind == JsonValueKind.Object
            ? currentItem : default;
        var trackId = item.ValueKind == JsonValueKind.Object ? StringValue(item, "id") : null;
        var title = item.ValueKind == JsonValueKind.Object ? StringValue(item, "name") : null;
        var duration = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("duration_ms", out var durationValue)
            && durationValue.ValueKind == JsonValueKind.Number ? durationValue.GetInt64() : 0;
        var artist = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("artists", out var artists)
            && artists.ValueKind == JsonValueKind.Array
                ? string.Join(", ", artists.EnumerateArray().Select(value => StringValue(value, "name")).Where(name => name != null))
                : null;

        return new SpotifyPlaybackSnapshot(true, isPlaying, !isPlaying && trackId != null, trackId, title, artist,
            target.Volume, progress, duration, target.Name);
    }

    public async Task<IReadOnlyList<SpotifyQueueTrack>> StartPlaybackAsync(
        IReadOnlyList<SpotifyQueueTrack> tracks,
        CancellationToken cancellationToken = default)
    {
        var candidates = tracks
            .Select(track => (Track: track, Uri: ToTrackUri(track.SpotifyId)))
            .Where(candidate => candidate.Uri != null)
            .Select(candidate => (candidate.Track, Uri: candidate.Uri!))
            .ToArray();
        if (candidates.Length == 0)
            return [];
        if (candidates.Length > 100)
            throw new SpotifyPlaybackException(StatusCodes.Status400BadRequest, "Spotify accepts no more than 100 track URIs in one playback request.");

        var target = await RequireConfiguredDeviceAsync(cancellationToken);
        var playableTracks = new List<SpotifyQueueTrack>(candidates.Length);
        foreach (var candidate in candidates)
        {
            var requestedId = candidate.Uri["spotify:track:".Length..];
            try
            {
                using var trackResponse = await SendApiAsync(
                    HttpMethod.Get,
                    $"/tracks/{Uri.EscapeDataString(requestedId)}?market=from_token",
                    null,
                    cancellationToken);
                using var trackJson = await ReadSuccessfulJsonAsync(trackResponse, cancellationToken);
                var track = trackJson.RootElement;
                if (track.TryGetProperty("is_playable", out var isPlayable) &&
                    isPlayable.ValueKind == JsonValueKind.False)
                {
                    _logger.LogInformation("Skipping Spotify track {SpotifyId}; it is not playable for the connected account.", requestedId);
                    continue;
                }

                var playableUri = ToTrackUri(StringValue(track, "uri") ?? StringValue(track, "id") ?? requestedId);
                if (playableUri == null)
                    continue;

                playableTracks.Add(candidate.Track with
                {
                    SpotifyId = playableUri["spotify:track:".Length..]
                });
            }
            catch (SpotifyPlaybackException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
            {
                _logger.LogInformation("Skipping Spotify track {SpotifyId}; Spotify no longer returns it.", requestedId);
            }
        }

        if (playableTracks.Count == 0)
            return [];

        var uris = playableTracks
            .Select(track => ToTrackUri(track.SpotifyId)!)
            .ToArray();
        var playPath = $"/me/player/play?device_id={Uri.EscapeDataString(target.Id)}";
        using var response = await SendApiAsync(HttpMethod.Put, playPath, JsonSerializer.Serialize(new { uris }), cancellationToken);
        await EnsureSuccessfulAsync(response, cancellationToken);
        return playableTracks;
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        var target = await RequireConfiguredDeviceAsync(cancellationToken);
        using var response = await SendApiAsync(HttpMethod.Put,
            $"/me/player/pause?device_id={Uri.EscapeDataString(target.Id)}", null, cancellationToken);
        await EnsureSuccessfulAsync(response, cancellationToken);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        var target = await RequireConfiguredDeviceAsync(cancellationToken);
        using var response = await SendApiAsync(HttpMethod.Put,
            $"/me/player/play?device_id={Uri.EscapeDataString(target.Id)}", null, cancellationToken);
        await EnsureSuccessfulAsync(response, cancellationToken);
    }

    public async Task SkipToNextAsync(CancellationToken cancellationToken = default)
    {
        var target = await RequireConfiguredDeviceAsync(cancellationToken);
        using var response = await SendApiAsync(HttpMethod.Post,
            $"/me/player/next?device_id={Uri.EscapeDataString(target.Id)}", null, cancellationToken);
        await EnsureSuccessfulAsync(response, cancellationToken);
    }

    public async Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
    {
        var target = await RequireConfiguredDeviceAsync(cancellationToken);
        var safeVolume = Math.Clamp(volume, 0, 100);
        using var response = await SendApiAsync(HttpMethod.Put,
            $"/me/player/volume?volume_percent={safeVolume}&device_id={Uri.EscapeDataString(target.Id)}",
            null, cancellationToken);
        await EnsureSuccessfulAsync(response, cancellationToken);
    }

    private async Task<ConnectDevice?> FindConfiguredDeviceAsync(CancellationToken cancellationToken)
    {
        var configuredName = _configuration["Spotify:DeviceName"];
        if (string.IsNullOrWhiteSpace(configuredName))
            throw new InvalidOperationException("Spotify:DeviceName must name the PA Spotify Connect device.");

        using var response = await SendApiAsync(HttpMethod.Get, "/me/player/devices", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;
        using var json = await ReadSuccessfulJsonAsync(response, cancellationToken);
        if (!json.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            return null;

        return devices.EnumerateArray()
            .Where(device => !device.TryGetProperty("is_restricted", out var restricted) || !restricted.GetBoolean())
            .Where(device => string.Equals(StringValue(device, "name"), configuredName, StringComparison.OrdinalIgnoreCase))
            .Select(device => new ConnectDevice(
                StringValue(device, "id") ?? "",
                StringValue(device, "name") ?? configuredName,
                device.TryGetProperty("volume_percent", out var volume) && volume.ValueKind == JsonValueKind.Number
                    ? volume.GetInt32() : 0,
                device.TryGetProperty("is_active", out var active) && active.GetBoolean()))
            .Where(device => !string.IsNullOrWhiteSpace(device.Id))
            .OrderByDescending(device => device.IsActive)
            .FirstOrDefault();
    }

    private async Task<ConnectDevice> RequireConfiguredDeviceAsync(CancellationToken cancellationToken)
    {
        var device = await FindConfiguredDeviceAsync(cancellationToken);
        return device ?? throw new SpotifyPlaybackException(StatusCodes.Status503ServiceUnavailable,
            $"Spotify Connect device '{_configuration["Spotify:DeviceName"]}' is unavailable.");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is { ExpiresAt: var expiry } cached && expiry > DateTime.UtcNow.AddSeconds(60))
            return cached.Value;

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is { ExpiresAt: var currentExpiry } current && currentExpiry > DateTime.UtcNow.AddSeconds(60))
                return current.Value;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var connection = await db.SpotifyPlaybackConnections.SingleOrDefaultAsync(item => item.Id == 1, cancellationToken)
                ?? throw new SpotifyPlaybackException(StatusCodes.Status503ServiceUnavailable, "Spotify playback account is not connected.");
            if (connection.RefreshTokenExpiresAt <= DateTime.UtcNow)
                throw new SpotifyPlaybackException(StatusCodes.Status401Unauthorized, "Spotify refresh token expired; an administrator must reconnect the account.");

            EnsureOAuthConfiguration();
            var refreshToken = DecryptRefreshToken(connection.RefreshTokenCiphertext);
            using var response = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = await response.Content.ReadAsStringAsync(cancellationToken);
                if (message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
                {
                    connection.RefreshTokenExpiresAt = DateTime.UtcNow;
                    connection.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    throw new SpotifyPlaybackException(StatusCodes.Status401Unauthorized,
                        "Spotify authorization expired or was revoked; an administrator must reconnect the account.");
                }
                throw await CreateSpotifyExceptionAsync(response, cancellationToken);
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = json.RootElement;
            var token = StringValue(root, "access_token");
            if (string.IsNullOrWhiteSpace(token))
                throw new SpotifyPlaybackException(StatusCodes.Status502BadGateway, "Spotify token refresh returned no access token.");

            var now = DateTime.UtcNow;
            var expiresIn = IntValue(root, "expires_in") ?? 3600;
            if (!string.IsNullOrWhiteSpace(StringValue(root, "refresh_token")))
            {
                connection.RefreshTokenCiphertext = EncryptRefreshToken(StringValue(root, "refresh_token")!);
                var newRefreshLifetime = IntValue(root, "refresh_token_expires_in");
                if (newRefreshLifetime is > 0)
                    connection.RefreshTokenExpiresAt = now.AddSeconds(newRefreshLifetime.Value);
                connection.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }

            _accessToken = new AccessTokenCache(token, now.AddSeconds(expiresIn));
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendApiAsync(
        HttpMethod method,
        string path,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetAccessTokenAsync(cancellationToken);
            using var request = new HttpRequestMessage(method, $"{ApiBaseUrl}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (jsonBody != null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var response = await _httpClientFactory.CreateClient("Spotify").SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt == 1)
                return response;

            response.Dispose();
            _accessToken = null;
        }

        throw new SpotifyPlaybackException(StatusCodes.Status401Unauthorized, "Spotify authorization failed.");
    }

    private async Task<HttpResponseMessage> RequestTokenAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        var clientId = _configuration["Spotify:ClientId"];
        var clientSecret = _configuration["Spotify:ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new SpotifyPlaybackException(StatusCodes.Status503ServiceUnavailable, "Spotify client credentials are not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AccountsBaseUrl}/api/token")
        {
            Content = new FormUrlEncodedContent(form)
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        return await _httpClientFactory.CreateClient("Spotify").SendAsync(request, cancellationToken);
    }

    private async Task<JsonDocument> ReadSuccessfulJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw await CreateSpotifyExceptionAsync(response, cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private static async Task EnsureSuccessfulAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw await CreateSpotifyExceptionAsync(response, cancellationToken);
    }

    private static async Task<SpotifyPlaybackException> CreateSpotifyExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        _ = await response.Content.ReadAsStringAsync(cancellationToken);
        string message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Spotify authorization failed; an administrator may need to reconnect the account.",
            HttpStatusCode.Forbidden => "Spotify rejected playback control. The account must have Premium and the requested permissions.",
            HttpStatusCode.NotFound => "Spotify could not find the playback device or track.",
            HttpStatusCode.TooManyRequests => "Spotify rate limit reached; retry the operation shortly.",
            _ => $"Spotify API request failed with HTTP {(int)response.StatusCode}."
        };
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (!retryAfter.HasValue && response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
            retryAfter = retryDate - DateTimeOffset.UtcNow;
        if (retryAfter < TimeSpan.Zero)
            retryAfter = TimeSpan.Zero;
        return new SpotifyPlaybackException((int)response.StatusCode, message, retryAfter);
    }

    private async Task<TokenResponse> ParseTokenResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw await CreateSpotifyExceptionAsync(response, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        return new TokenResponse(
            StringValue(root, "access_token"),
            StringValue(root, "refresh_token"),
            StringValue(root, "scope"),
            IntValue(root, "expires_in") ?? 3600,
            IntValue(root, "refresh_token_expires_in"));
    }

    private string EncryptRefreshToken(string refreshToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(refreshToken);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(GetEncryptionKey(), tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]);
    }

    private string DecryptRefreshToken(string encrypted)
    {
        var value = Convert.FromBase64String(encrypted);
        if (value.Length < 28)
            throw new InvalidOperationException("Stored Spotify refresh token ciphertext is invalid.");
        var plaintext = new byte[value.Length - 28];
        using var aes = new AesGcm(GetEncryptionKey(), 16);
        aes.Decrypt(value.AsSpan(0, 12), value.AsSpan(28), value.AsSpan(12, 16), plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private byte[] GetEncryptionKey()
    {
        var setting = _configuration["Spotify:TokenEncryptionKey"];
        if (string.IsNullOrWhiteSpace(setting))
            throw new SpotifyPlaybackException(StatusCodes.Status503ServiceUnavailable, "Spotify token encryption key is not configured in deployment secrets.");
        try
        {
            var key = Convert.FromBase64String(setting);
            if (key.Length != 32)
                throw new FormatException();
            return key;
        }
        catch (FormatException)
        {
            throw new SpotifyPlaybackException(StatusCodes.Status503ServiceUnavailable,
                "Spotify:TokenEncryptionKey must be a base64-encoded 32-byte key.");
        }
    }

    private bool HasPlaybackConfiguration() =>
        !string.IsNullOrWhiteSpace(_configuration["Spotify:ClientId"]) &&
        !string.IsNullOrWhiteSpace(_configuration["Spotify:ClientSecret"]) &&
        !string.IsNullOrWhiteSpace(_configuration["Spotify:RedirectUri"]) &&
        !string.IsNullOrWhiteSpace(_configuration["Spotify:DeviceName"]) &&
        !string.IsNullOrWhiteSpace(_configuration["Spotify:TokenEncryptionKey"]);

    private void EnsureOAuthConfiguration()
    {
        if (!HasPlaybackConfiguration())
            throw new SpotifyPlaybackException(StatusCodes.Status503ServiceUnavailable,
                "Spotify ClientId, ClientSecret, RedirectUri, DeviceName, and TokenEncryptionKey must be configured.");
        _ = GetEncryptionKey();
    }

    private static string? ToTrackUri(string spotifyId)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
            return null;
        var id = spotifyId.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? spotifyId["spotify:track:".Length..]
            : spotifyId;
        if (id.Length is < 10 or > 64 || id.Any(character => !char.IsAsciiLetterOrDigit(character)))
            return null;
        return $"spotify:track:{id}";
    }

    private static string? StringValue(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? IntValue(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var result)
            ? result
            : null;

    private sealed record AccessTokenCache(string Value, DateTime ExpiresAt);
    private sealed record ConnectDevice(string Id, string Name, int Volume, bool IsActive);
    private sealed record TokenResponse(string? AccessToken, string? RefreshToken, string? Scope, int ExpiresIn, int? RefreshTokenExpiresIn);
}
