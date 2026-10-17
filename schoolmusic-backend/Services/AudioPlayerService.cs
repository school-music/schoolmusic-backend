using LibVLCSharp.Shared;
using Microsoft.AspNetCore.SignalR;
using schoolmusic_backend.Hubs;

namespace schoolmusic_backend.Services
{
    /// <summary>
    /// Reprezentuje aktualny stan odtwarzacza multimediów.
    /// </summary>
    /// <remarks>Wartość Source określa typ źródła; wartości czasowe (CurrentPositionMs, TotalDurationMs) są
    /// podane w milisekundach.</remarks>
    public record PlayerStatusDto(
        bool IsPlaying,
        bool IsPaused,
        int Volume,
        bool IsMuted,
        string Source, // None, LocalAudio, Url, Spotify
        string? Title,
        string? Artist,
        long CurrentPositionMs,
        long TotalDurationMs
    );

    public interface IAudioPlayerService
    {
        int GetVolume();
        void SetVolume(int volume);
        void VolumeUp(int step = 5);
        void VolumeDown(int step = 5);
        void ToggleMute();
        void SetMute(bool mute);

        PlayerStatusDto GetStatus();
        Task<ServiceResult<PlayerStatusDto>> PlayFileOrUrlAsync(string pathOrUrl, string? title = null, string? artist = null);
        Task<ServiceResult<PlayerStatusDto>> PauseAsync();
        Task<ServiceResult<PlayerStatusDto>> ResumeAsync();
        Task<ServiceResult<PlayerStatusDto>> StopAsync();

        /// <summary>
        /// Zdarzenie wywoływane po zakończeniu odtwarzania utworu
        /// </summary>
        event Action? MediaEnded;
    }

    /// <summary>
    /// Udostępnia odtwarzanie audio przy użyciu LibVLC; zarządza głośnością, kontrolą odtwarzania i rozsyłaniem statusu
    /// do klientów SignalR.
    /// </summary>
    public class AudioPlayerService : IAudioPlayerService, IDisposable
    {
        private readonly LibVLCSharp.Shared.LibVLC _libVLC;
        private readonly MediaPlayer _mediaPlayer;
        private readonly IHubContext<QueueHub> _hubContext;
        private readonly object _lock = new();

        private string _currentSource = "None";
        private string? _currentTitle;
        private string? _currentArtist;

        public event Action? MediaEnded;

        public AudioPlayerService(IHubContext<QueueHub> hubContext)
        {
            _hubContext = hubContext;

            string[] vlcOptions =
            {
                "--file-caching=2000",
                "--network-caching=2000",
                "--clock-jitter=0",
                "--aout=directsound"
            };

            _libVLC = new LibVLCSharp.Shared.LibVLC(enableDebugLogs: false, vlcOptions);
            _mediaPlayer = new MediaPlayer(_libVLC)
            {
                Volume = 50 // Domyślna głośność 50%
            };

            // Eventy LibVLC
            _mediaPlayer.EndReached += (s, e) =>
            {
                Task.Run(async () =>
                {
                    lock (_lock)
                    {
                        _currentSource = "None";
                        _currentTitle = null;
                        _currentArtist = null;
                    }
                    await BroadcastPlaybackStatusAsync();
                    try
                    {
                        MediaEnded?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AudioPlayerService] Błąd w obsłudze MediaEnded: {ex.Message}");
                    }
                });
            };

            _mediaPlayer.EncounteredError += (s, e) =>
            {
                Task.Run(async () =>
                {
                    lock (_lock)
                    {
                        _currentSource = "None";
                    }
                    await BroadcastPlaybackStatusAsync();
                });
            };
        }

        // głośność

        /// <summary>
        /// Zwraca aktualną głośność
        /// </summary>
        public int GetVolume()
        {
            lock (_lock)
            {
                return _mediaPlayer.Volume;
            }
        }

        /// <summary>
        /// Ustawia głośność na podaną
        /// </summary>
        /// <param name="volume">Oczekiwana głośność</param>
        public void SetVolume(int volume)
        {
            lock (_lock)
            {
                int clamped = Math.Clamp(volume, 0, 150);
                _mediaPlayer.Volume = clamped;
                if (clamped > 0 && _mediaPlayer.Mute)
                {
                    _mediaPlayer.Mute = false;
                }
            }
            _ = BroadcastVolumeStatusAsync();
        }

        /// <summary>
        /// Dwie funkcje które mają na celu zwiększenie/zmiejszenie głośności,
        /// tak jak guziki na telefonie, domyślnie o 5
        /// </summary>
        public void VolumeUp(int step = 5) => SetVolume(_mediaPlayer.Volume + step);
        public void VolumeDown(int step = 5) => SetVolume(_mediaPlayer.Volume - step);

        /// <summary>
        /// Wycisza lub odcisza głośnik w zależności od wcześniejszego 
        /// stanu wyciszenia
        /// </summary>
        public void ToggleMute()
        {
            lock (_lock)
            {
                _mediaPlayer.Mute = !_mediaPlayer.Mute;
            }
            _ = BroadcastVolumeStatusAsync();
        }

        /// <summary>
        /// Ustawia stan wyciszenia odtwarzacza multimedialnego.
        /// </summary>
        /// <remarks>Operacja jest synchronizowana przez blok lock dla bezpieczeństwa wątków. Zmiana stanu
        /// jest rozgłaszana asynchronicznie przez BroadcastVolumeStatusAsync.</remarks>
        public void SetMute(bool mute)
        {
            lock (_lock)
            {
                _mediaPlayer.Mute = mute;
            }
            _ = BroadcastVolumeStatusAsync();
        }

        // odtwarzanie

        /// <summary>
        /// Aktualny status odtwarzania
        /// </summary>
        /// <returns>Kiedyś to napisze</returns>
        public PlayerStatusDto GetStatus()
        {
            lock (_lock)
            {
                bool isPlaying = _mediaPlayer.IsPlaying;
                bool isPaused = _mediaPlayer.State == VLCState.Paused;
                long time = _mediaPlayer.Time;
                long length = _mediaPlayer.Length;

                return new PlayerStatusDto(
                    IsPlaying: isPlaying,
                    IsPaused: isPaused,
                    Volume: _mediaPlayer.Volume,
                    IsMuted: _mediaPlayer.Mute,
                    Source: _currentSource,
                    Title: _currentTitle,
                    Artist: _currentArtist,
                    CurrentPositionMs: time > 0 ? time : 0,
                    TotalDurationMs: length > 0 ? length : 0
                );
            }
        }

        /// <summary>
        /// Odtwarza podany link lub plik
        /// </summary>
        /// <param name="pathOrUrl">Link lub ścieżka do pliku docelowego</param>
        /// <param name="title">Tytuł utworu</param>
        /// <param name="artist">Nazwa wykonwacy</param>
        public async Task<ServiceResult<PlayerStatusDto>> PlayFileOrUrlAsync(string pathOrUrl, string? title = null, string? artist = null)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl))
            {
                return ServiceResult<PlayerStatusDto>.BadRequest("Ścieżka do pliku lub adres URL nie mogą być puste.");
            }

            try
            {
                Media media;
                string source;

                if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    // Bezpieczne pobranie strumienia do pliku na dysku
                    // Rozwiązuje błędy 'cache_read stream error: cannot pre fill buffer' i demux w LibVLC na Windows
                    string cacheFile = Path.Combine(Path.GetTempPath(), "schoolmusic_stream_cache.mp3");
                    using (var httpClient = new HttpClient())
                    {
                        var bytes = await httpClient.GetByteArrayAsync(uri);
                        await File.WriteAllBytesAsync(cacheFile, bytes);
                    }

                    media = new Media(_libVLC, cacheFile, FromType.FromPath);
                    source = "Url";
                    title ??= Path.GetFileName(uri.LocalPath);
                }
                else if (File.Exists(pathOrUrl))
                {
                    media = new Media(_libVLC, pathOrUrl, FromType.FromPath);
                    source = "LocalAudio";
                    title ??= Path.GetFileNameWithoutExtension(pathOrUrl);
                }
                else
                {
                    return ServiceResult<PlayerStatusDto>.NotFound($"Nie znaleziono pliku: '{pathOrUrl}' ani nie jest to poprawny URL HTTP/HTTPS.");
                }

                lock (_lock)
                {
                    _mediaPlayer.Stop();
                    _currentSource = source;
                    _currentTitle = string.IsNullOrWhiteSpace(title) ? "Nagranie radiowęzła" : title;
                    _currentArtist = string.IsNullOrWhiteSpace(artist) ? "Szkolny radiowęzeł" : artist;
                    _mediaPlayer.Play(media);
                }

                await BroadcastPlaybackStatusAsync();

                return ServiceResult<PlayerStatusDto>.Ok(GetStatus(), "Rozpoczęto odtwarzanie.");
            }
            catch (Exception ex)
            {
                return ServiceResult<PlayerStatusDto>.BadRequest($"Błąd podczas uruchamiania odtwarzania: {ex.Message}");
            }
        }

        /// <summary>
        /// Zatrzymuje odtwarzanie 
        /// </summary>
        public async Task<ServiceResult<PlayerStatusDto>> PauseAsync()
        {
            lock (_lock)
            {
                if (!_mediaPlayer.IsPlaying && _mediaPlayer.State != VLCState.Buffering)
                {
                    return ServiceResult<PlayerStatusDto>.BadRequest("Odtwarzacz nie odtwarza obecnie żadnego utworu.");
                }
                _mediaPlayer.SetPause(true);
            }

            await BroadcastPlaybackStatusAsync();
            return ServiceResult<PlayerStatusDto>.Ok(GetStatus(), "Odtwarzanie wstrzymane.");
        }

        /// <summary>
        /// Wznawia odtwarzanie
        /// </summary>
        public async Task<ServiceResult<PlayerStatusDto>> ResumeAsync()
        {
            lock (_lock)
            {
                if (_mediaPlayer.State != VLCState.Paused)
                {
                    return ServiceResult<PlayerStatusDto>.BadRequest("Odtwarzacz nie jest obecnie wstrzymany.");
                }
                _mediaPlayer.SetPause(false);
            }

            await BroadcastPlaybackStatusAsync();
            return ServiceResult<PlayerStatusDto>.Ok(GetStatus(), "Wznowiono odtwarzanie.");
        }

        /// <summary>
        /// Zatrzymuje odtwarzanie, resetuje informacje o aktualnym źródle i metadanych utworu 
        /// oraz rozsyła zaktualizowany stan odtwarzacza.
        /// </summary>
        public async Task<ServiceResult<PlayerStatusDto>> StopAsync()
        {
            lock (_lock)
            {
                _mediaPlayer.Stop();
                _currentSource = "None";
                _currentTitle = null;
                _currentArtist = null;
            }

            await BroadcastPlaybackStatusAsync();
            return ServiceResult<PlayerStatusDto>.Ok(GetStatus(), "Zatrzymano odtwarzanie.");
        }

        // websockety - SignalR

        /// <summary>
        /// Wysyła do wszystkich klientów SignalR komunikat o zmianie głośności 
        /// zawierający aktualne wartości głośności i stanu wyciszenia.
        /// </summary>
        private async Task BroadcastVolumeStatusAsync()
        {
            await _hubContext.Clients.All.SendAsync("VolumeChanged", new
            {
                volume = _mediaPlayer.Volume,
                isMuted = _mediaPlayer.Mute
            });
        }

        /// <summary>
        /// Wysyła do wsyztskich klientów SignalR komunikat o zmianie odtwarzania
        /// </summary>
        private async Task BroadcastPlaybackStatusAsync()
        {
            var status = GetStatus();
            await _hubContext.Clients.All.SendAsync("PlaybackStatusChanged", status);
            await _hubContext.Clients.Group(QueueHub.KioskGroup).SendAsync("NowPlayingChanged", status);
        }

        /// <summary>
        /// Zwalnia zasoby zarządzane używane przez AudioPlayerService
        /// </summary>
        public void Dispose()
        {
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
        }
    }
}
