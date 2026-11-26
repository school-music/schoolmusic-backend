using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers
{
    /// <summary>
    /// Kontroler API udostępniający statystyki dla panelu administratora
    /// </summary>
    /// <remarks>Dostęp wyłącznie dla ról 'admin' i 'head_admin'</remarks>
    [Route("api/statistics")]
    [ApiController]
    [Authorize(Roles = "admin,head_admin")]
    [Produces("application/json")]
    public class StatisticsController : ControllerBase
    {
        private readonly IStatisticsService _statisticsService;

        public StatisticsController(IStatisticsService statisticsService)
        {
            _statisticsService = statisticsService;
        }

        /// <summary>
        /// Zwraca komplet statystyk dla dashboardu: liczbę oddanych głosów, rozkład gatunków,
        /// aktywność z ostatnich 7 dni oraz TOP 10 najczęściej odtwarzanych utworów i wykonawców.
        /// </summary>
        [HttpGet("dashboard")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        [ProducesResponseType<DashboardStatisticsDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> GetDashboard(CancellationToken cancellationToken)
        {
            try
            {
                var dashboard = await _statisticsService.GetDashboardAsync(cancellationToken);
                return Ok(dashboard);
            }
            catch (StatisticsUnavailableException ex)
            {
                Response.Headers.RetryAfter = "15";
                return this.Create(
                    StatusCodes.Status503ServiceUnavailable,
                    ex.Message,
                    extensions: new Dictionary<string, object?>
                    {
                        ["isAvailable"] = false,
                        ["retryAfter"] = 15
                    });
            }
        }
    }
}
