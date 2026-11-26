using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers
{
    public sealed record TopSongsRankingResponseDto(
        string Period,
        int Total,
        IReadOnlyList<TopSongRankingDto> Songs
    );

    public sealed record TopArtistsRankingResponseDto(
        string Period,
        int Total,
        IReadOnlyList<TopArtistRankingDto> Artists
    );

    public sealed record PlaybackHistoryResponseDto(
        int Total,
        IReadOnlyList<PlaybackHistoryDto> History
    );

    /// <summary>
    /// Kontroler API udostępniający końcówki do pobierania rankingów najpopularniejszych utworów i wykonawców oraz
    /// historii ostatnich odtworzeń
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [AllowAnonymous]
    [Produces("application/json")]
    public class RankingsController : ControllerBase
    {
        private readonly IRankingsService _rankingsService;

        public RankingsController(IRankingsService rankingsService)
        {
            _rankingsService = rankingsService;
        }

        /// <summary>
        /// Zwraca ranking najpopularniejszych utworów w szkole.
        /// Parametr period: "day", "week" (domyślnie), "month", "year", "all-time".
        /// </summary>
        [HttpGet("songs")]
        [ProducesResponseType<TopSongsRankingResponseDto>(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetTopSongs(
            [FromQuery] string? period = "week",
            [FromQuery] int limit = 10)
        {
            var ranking = await _rankingsService.GetTopSongsAsync(period, limit);
            return Ok(new TopSongsRankingResponseDto(
                Period: period ?? "week",
                Total: ranking.Count,
                Songs: ranking
            ));
        }

        /// <summary>
        /// Zwraca ranking najczęściej odtwarzanych wykonawców w szkole.
        /// Parametr period: "day", "week" (domyślnie), "month", "year", "all-time".
        /// </summary>
        [HttpGet("artists")]
        [ProducesResponseType<TopArtistsRankingResponseDto>(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetTopArtists(
            [FromQuery] string? period = "week",
            [FromQuery] int limit = 10)
        {
            var ranking = await _rankingsService.GetTopArtistsAsync(period, limit);
            return Ok(new TopArtistsRankingResponseDto(
                Period: period ?? "week",
                Total: ranking.Count,
                Artists: ranking
            ));
        }

        /// <summary>
        /// Zwraca historię ostatnio zagranych utworów na radiowęźle (oś czasu).
        /// </summary>
        [HttpGet("history")]
        [ProducesResponseType<PlaybackHistoryResponseDto>(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetRecentHistory(
            [FromQuery] int limit = 20,
            [FromQuery] int offset = 0)
        {
            var history = await _rankingsService.GetRecentHistoryAsync(limit, offset);
            return Ok(new PlaybackHistoryResponseDto(
                Total: history.Count,
                History: history
            ));
        }
    }
}
