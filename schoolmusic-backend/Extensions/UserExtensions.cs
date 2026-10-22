using Microsoft.EntityFrameworkCore;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Extensions
{
    public static class UserExtensions
    {
        /// <summary>
        /// Asynchronicznie sprawdza, czy użytkownik o podanym identyfikatorze ma rangę administratora (RankId >= 3).
        /// </summary>
        /// <returns>Task&lt;bool&gt; zawierający true, jeśli użytkownik istnieje i ma RankId &gt;= 3; w przeciwnym razie false.</returns>
        public static Task<bool> IsAdminAsync(this schoolmusicContext context, int uId)
        {
            return context.Users.AnyAsync(u => u.Id == uId && u.RankId >= 3);
        }
    }
}
