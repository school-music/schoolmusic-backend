using Microsoft.AspNetCore.SignalR;

namespace schoolmusic_backend.Hubs
{
    public class QueueHub : Hub
    {
        public async Task JoinBreak(int breakId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"Break_{breakId}");
        }
        public async Task LeaveBreak(int breakId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Break_{breakId}");
        }
    }
}
