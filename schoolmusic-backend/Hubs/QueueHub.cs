using Microsoft.AspNetCore.SignalR;

namespace schoolmusic_backend.Hubs
{
    /// <summary>
    /// Hub SignalR umożliwiający zarządzanie uczestnictwem klientów w grupach odpowiadających przerwom.
    /// </summary>
    /// <remarks>Grupy identyfikowane są wzorcem 'Break_{breakId}'. Metody asynchronicznie dodają lub usuwają
    /// połączenie klienta z odpowiedniej grupy.</remarks>
    public class QueueHub : Hub
    {
        /// <summary>
        /// Dołącza bieżące połączenie do grupy SignalR o nazwie 'Break_{breakId}'.
        /// </summary>
        public async Task JoinBreak(int breakId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"Break_{breakId}");
        }

        /// <summary>
        /// Dołącza bieżące połączenie do grupy kiosków otrzymującej zdarzenia stanu szkoły.
        /// </summary>
        public Task JoinKiosk() => Groups.AddToGroupAsync(Context.ConnectionId, KioskGroup);

        public const string KioskGroup = "Kiosk";
        /// <summary>
        /// Usuwa bieżące połączenie z grupy SignalR o nazwie "Break_{breakId}".
        /// </summary>
        /// <remarks>Usuwa jedynie połączenie reprezentowane przez Context.ConnectionId.</remarks>
        public async Task LeaveBreak(int breakId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Break_{breakId}");
        }
    }
}