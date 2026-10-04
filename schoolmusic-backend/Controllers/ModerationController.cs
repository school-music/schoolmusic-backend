using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using schoolmusic_backend.Models;
using StackExchange.Redis;
using System.Text.Json;


namespace schoolmusic_backend.Controllers
{
    [Route("/api/[controller]")]
    [ApiController]
    [Authorize(Roles ="mod,admin,head_admin")]
    public class ModerationController : Controller
    {
        private readonly schoolmusicContext _context;
        private readonly IConnectionMultiplexer _redis;
        public ModerationController(schoolmusicContext context, IConnectionMultiplexer redis)
        {
            _context = context;
            _redis = redis;
        }
        public record SongProposalDto(
            string Id,
            string SongId,
            string SpotifyId,
            string Title,
            string Artist,
            string Cover,
            int DurationMs,
            string BreakId,
            int BreakNumber,
            string UserId,
            string SubmittedBy,
            DateTime SubmittedAt
        );
        [HttpGet("pending")]
        public async Task<IActionResult> GetAllPendingSongs()
        {
            var db = _redis.GetDatabase();
            string pendingKey = "proposals:pending";

            RedisValue[] pendingSongs = await db.ListRangeAsync(pendingKey);

            List<SongProposalDto> proposals = pendingSongs
                .Where(rv => rv.HasValue) // element is not null
                .Select(rv => JsonSerializer.Deserialize<SongProposalDto>(rv.ToString()))
                .Where(dto => dto != null) // ignore empty deserialization results
                .ToList();

            return Ok(new
            {
                Message = "Wszystkie propozycje piosenek oczekujące na zatwierdzenie",
                Songs = proposals,
            });
        }



        [HttpPost("approve/{proposalId:int}")]
        public Task<IActionResult> ApproveSong(int proposalId)
        {


            return null;
        }
    }
}
