using SpotifyAPI.Web;

namespace schoolmusic_backend.Services
{
    /// <summary>
    /// Implementacja integracji ze Spotify Web API z obsługą graceful fallback w przypadku braku kluczy
    /// </summary>
    public class SpotifySongSource : ISongSourceService
    {
        private readonly string? _clientId;
        private readonly string? _clientSecret;
        private readonly ILogger<SpotifySongSource> _logger;
        private SpotifyClient? _client;
        private readonly SemaphoreSlim _initLock = new(1, 1);

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_clientId) && !string.IsNullOrWhiteSpace(_clientSecret);

        public SpotifySongSource(IConfiguration configuration, ILogger<SpotifySongSource> logger)
        {
            _logger = logger;
            _clientId = configuration["Spotify:ClientId"] ?? configuration["SPOTIFY_CLIENT_ID"];
            _clientSecret = configuration["Spotify:ClientSecret"] ?? configuration["SPOTIFY_CLIENT_SECRET"];

            if (!IsConfigured)
            {
                _logger.LogInformation("SpotifySongSource zainicjalizowany w trybie gotowości (brak skonfigurowanych kluczy Spotify ClientId/ClientSecret).");
            }
            else
            {
                _logger.LogInformation("SpotifySongSource skonfigurowany z poświadczeniami ClientId.");
            }
        }

        private async Task<SpotifyClient?> GetClientAsync()
        {
            if (!IsConfigured) return null;
            if (_client != null) return _client;

            await _initLock.WaitAsync();
            try
            {
                if (_client != null) return _client;

                var config = SpotifyClientConfig.CreateDefault();
                var request = new ClientCredentialsRequest(_clientId!, _clientSecret!);
                var response = await new OAuthClient(config).RequestToken(request);

                _client = new SpotifyClient(config.WithToken(response.AccessToken));
                return _client;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Błąd podczas uwierzytelniania ze Spotify API: {Message}", ex.Message);
                return null;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<IEnumerable<ExternalTrackDto>> SearchTracksAsync(string query, int limit = 10)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(query))
            {
                _logger.LogDebug("Wyszukiwanie Spotify pominięte (brak konfiguracji lub puste zapytanie).");
                return Enumerable.Empty<ExternalTrackDto>();
            }

            try
            {
                var client = await GetClientAsync();
                if (client == null) return Enumerable.Empty<ExternalTrackDto>();

                var searchRequest = new SearchRequest(SearchRequest.Types.Track, query)
                {
                    Limit = Math.Clamp(limit, 1, 10)
                };

                var searchResult = await client.Search.Item(searchRequest);
                if (searchResult.Tracks?.Items == null)
                {
                    return Enumerable.Empty<ExternalTrackDto>();
                }

                return searchResult.Tracks.Items.Select(t => new ExternalTrackDto(
                    ExternalId: t.Id,
                    Title: t.Name,
                    ArtistName: t.Artists.FirstOrDefault()?.Name ?? "Nieznany",
                    CoverUrl: t.Album.Images.FirstOrDefault()?.Url,
                    DurationMs: t.DurationMs,
                    PreviewUrl: t.PreviewUrl,
                    Source: "spotify"
                )).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Błąd podczas wyszukiwania w Spotify API: {Message}", ex.Message);
                return Enumerable.Empty<ExternalTrackDto>();
            }
        }

        public async Task<ExternalTrackDto?> GetTrackByIdAsync(string externalId)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(externalId))
            {
                return null;
            }

            try
            {
                var client = await GetClientAsync();
                if (client == null) return null;

                var cleanId = externalId.Trim();
                if (cleanId.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
                {
                    cleanId = cleanId["spotify:track:".Length..];
                }
                else if (cleanId.Contains("/track/"))
                {
                    var part = cleanId[(cleanId.IndexOf("/track/", StringComparison.OrdinalIgnoreCase) + 7)..];
                    var qIndex = part.IndexOf('?');
                    cleanId = qIndex >= 0 ? part[..qIndex] : part;
                }

                var track = await client.Tracks.Get(cleanId);
                if (track == null) return null;

                return new ExternalTrackDto(
                    ExternalId: track.Id,
                    Title: track.Name,
                    ArtistName: track.Artists.FirstOrDefault()?.Name ?? "Nieznany",
                    CoverUrl: track.Album.Images.FirstOrDefault()?.Url,
                    DurationMs: track.DurationMs,
                    PreviewUrl: track.PreviewUrl,
                    Source: "spotify"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Błąd podczas pobierania utworu ze Spotify API ({Id}): {Message}", externalId, ex.Message);
                return null;
            }
        }
    }
}
