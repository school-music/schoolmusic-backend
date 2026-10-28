using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    public class StatisticsController : ControllerBase
    {
        private readonly IStatisticsService _statisticsService;

        public StatisticsController(IStatisticsService statisticsService)
        {
            _statisticsService = statisticsService;
        }

        /// <summary>
        /// Zwraca komplet statystyk dla dashboardu: liczbę oddanych głosów (ogółem, w bieżącym tygodniu, dzisiaj),
        /// rozkład najpopularniejszych gatunków, aktywność z ostatnich 7 dni oraz TOP 10 najczęściej odtwarzanych
        /// utworów i wykonawców.
        /// </summary>
        /// <remarks>
        /// Wynik jest cache'owany w Redis na 60 sekund, więc dane mogą być nieaktualne maksymalnie o ten czas.
        /// Liczniki głosów obejmują wyłącznie aktualnie otwarte przerwy — po archiwizacji przerwy jej głosy
        /// są usuwane z bazy i pozostają jedynie w historii odtworzeń.
        /// </remarks>
        [HttpGet("dashboard")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
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
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    StatusCode = 503,
                    Message = ex.Message,
                    IsAvailable = false,
                    RetryAfter = 15
                });
            }
        }
    }
}