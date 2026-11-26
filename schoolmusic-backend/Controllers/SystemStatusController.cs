using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers
{
    [Route("api/system")]
    [ApiController]
    [Produces("application/json")]
    public class SystemStatusController : ControllerBase
    {
        private readonly ISystemStatusService _statusService;

        public SystemStatusController(ISystemStatusService statusService)
        {
            _statusService = statusService;
        }

        /// <summary>
        /// Status szkoły, licznik do końca/początku przerwy, aktualny utwór i TOP 5 kolejki.
        /// </summary>
        [HttpGet("status")]
        [AllowAnonymous]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        [ProducesResponseType<SystemStatusDto>(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
        {
            var status = await _statusService.GetStatusAsync(cancellationToken);
            return Ok(status);
        }
    }
}
