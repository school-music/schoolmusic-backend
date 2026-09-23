using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace schoolmusic_backend.Models
{
    public class schoolmusicContext : DbContext
    {
        public schoolmusicContext(DbContextOptions<schoolmusicContext> options) 
            : base(options)
        {
        }
        public DbSet<User> Users { get; set; } = null!;
    }
}
