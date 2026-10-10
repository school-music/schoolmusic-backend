using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [AllowAnonymous]
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
        public async Task<IActionResult> GetTopSongs(
            [FromQuery] string? period = "week", 
            [FromQuery] int limit = 10)
        {
            var ranking = await _rankingsService.GetTopSongsAsync(period, limit);
            return Ok(new
            {
                period = period ?? "week",
                total = ranking.Count,
                songs = ranking
            });
        }

        /// <summary>
        /// Zwraca ranking najczęściej odtwarzanych wykonawców w szkole.
        /// Parametr period: "day", "week" (domyślnie), "month", "year", "all-time".
        /// </summary>
        [HttpGet("artists")]
        public async Task<IActionResult> GetTopArtists(
            [FromQuery] string? period = "week", 
            [FromQuery] int limit = 10)
        {
            var ranking = await _rankingsService.GetTopArtistsAsync(period, limit);
            return Ok(new
            {
                period = period ?? "week",
                total = ranking.Count,
                artists = ranking
            });
        }

        /// <summary>
        /// Zwraca historię ostatnio zagranych utworów na radiowęźle (oś czasu).
        /// </summary>
        [HttpGet("history")]
        public async Task<IActionResult> GetRecentHistory(
            [FromQuery] int limit = 20, 
            [FromQuery] int offset = 0)
        {
            var history = await _rankingsService.GetRecentHistoryAsync(limit, offset);
            return Ok(new
            {
                total = history.Count,
                history
            });
        }
    }
}