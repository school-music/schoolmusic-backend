using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

public partial class schoolmusicContext : DbContext
{
    public schoolmusicContext()
    {
    }

    public schoolmusicContext(DbContextOptions<schoolmusicContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Artist> Artists { get; set; }

    public virtual DbSet<ArtistBlacklist> ArtistBlacklists { get; set; }

    public virtual DbSet<BannedUser> BannedUsers { get; set; }

    public virtual DbSet<Break> Breaks { get; set; }

    public virtual DbSet<DefaultSong> DefaultSongs { get; set; }

    public virtual DbSet<ExceptionBreak> ExceptionBreaks { get; set; }

    public virtual DbSet<ExceptionDay> ExceptionDays { get; set; }

    public virtual DbSet<History> Histories { get; set; }

    public virtual DbSet<Moderator> Moderators { get; set; }

    public virtual DbSet<QueueItem> QueueItems { get; set; }

    public virtual DbSet<Rank> Ranks { get; set; }

    public virtual DbSet<Song> Songs { get; set; }

    public virtual DbSet<SongBlacklist> SongBlacklists { get; set; }

    public virtual DbSet<SongGreenlist> SongGreenlists { get; set; }

    public virtual DbSet<SpecialEvent> SpecialEvents { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<Vote> Votes { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .UseCollation("utf8mb4_polish_ci")
            .HasCharSet("utf8mb4");

        modelBuilder.Entity<Artist>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("artists");

            entity.HasIndex(e => e.SpotifyArtistId, "uniq_spotify_artist").IsUnique();

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.Name)
                .HasColumnType("text")
                .HasColumnName("name");
            entity.Property(e => e.SpotifyArtistId).HasColumnName("spotify_artist_id");
        });

        modelBuilder.Entity<ArtistBlacklist>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("artist_blacklist");

            entity.HasIndex(e => e.ArtistId, "artist_id").IsUnique();

            entity.HasIndex(e => e.ModeratorId, "moderator_id");

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.ArtistId)
                .HasColumnType("int(11)")
                .HasColumnName("artist_id");
            entity.Property(e => e.ModeratorId)
                .HasColumnType("int(11)")
                .HasColumnName("moderator_id");

            entity.HasOne(d => d.Artist).WithOne(p => p.ArtistBlacklist)
                .HasForeignKey<ArtistBlacklist>(d => d.ArtistId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("artist_blacklist_ibfk_1");

            entity.HasOne(d => d.Moderator).WithMany(p => p.ArtistBlacklists)
                .HasForeignKey(d => d.ModeratorId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("artist_blacklist_ibfk_2");
        });

        modelBuilder.Entity<BannedUser>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("banned_users");

            entity.HasIndex(e => e.ModeratorId, "moderator_id");

            entity.HasIndex(e => e.UserId, "user_id");

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.BannedAt)
                .HasDefaultValueSql("current_timestamp()")
                .HasColumnType("timestamp")
                .HasColumnName("banned_at");
            entity.Property(e => e.ModeratorId)
                .HasColumnType("int(11)")
                .HasColumnName("moderator_id");
            entity.Property(e => e.Reason)
                .HasColumnType("text")
                .HasColumnName("reason");
            entity.Property(e => e.UserId)
                .HasColumnType("int(11)")
                .HasColumnName("user_id");

            entity.HasOne(d => d.Moderator).WithMany(p => p.BannedUsers)
                .HasForeignKey(d => d.ModeratorId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("banned_users_ibfk_2");

            entity.HasOne(d => d.User).WithMany(p => p.BannedUsers)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("banned_users_ibfk_1");
        });

        modelBuilder.Entity<Break>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("break");

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.BreakNumber)
                .HasColumnType("int(11)")
                .HasColumnName("break_number");
            entity.Property(e => e.EndsAt)
                .HasColumnType("timestamp")
                .HasColumnName("ends_at");
            entity.Property(e => e.StartAt)
                .HasDefaultValueSql("current_timestamp()")
                .HasColumnType("timestamp")
                .HasColumnName("start_at");
            entity.Property(e => e.VoteEnds)
                .HasColumnType("timestamp")
                .HasColumnName("vote_ends");
        });

        modelBuilder.Entity<DefaultSong>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("default_songs");

            entity.HasIndex(e => e.SongId, "song_id");

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.SongId)
                .HasColumnType("int(11)")
                .HasColumnName("song_id");

            entity.HasOne(d => d.Song).WithMany(p => p.DefaultSongs)
                .HasForeignKey(d => d.SongId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("default_songs_ibfk_1");
        });

        modelBuilder.Entity<ExceptionBreak>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("exception_break");

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.Description)
                .HasColumnType("text")
                .HasColumnName("description");
            entity.Property(e => e.EndsAt)
                .HasColumnType("datetime")
                .HasColumnName("ends_at");
            entity.Property(e => e.StartsAt)
                .HasColumnType("datetime")
                .HasColumnName("starts_at");
        });

        modelBuilder.Entity<ExceptionDay>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("exception_day");

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.Description)
                .HasColumnType("text")
                .HasColumnName("description");
            entity.Property(e => e.EndsAt)
                .HasColumnType("timestamp")
                .HasColumnName("ends_at");
            entity.Property(e => e.StartAt)
                .HasDefaultValueSql("current_timestamp()")
                .HasColumnType("timestamp")
                .HasColumnName("start_at");
        });

        modelBuilder.Entity<History>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("history");

            entity.HasIndex(e => e.BreakId, "break_id");

            entity.HasIndex(e => e.SongId, "song_id");

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.BreakId)
                .HasColumnType("int(11)")
                .HasColumnName("break_id");
            entity.Property(e => e.PlayedAt)
                .HasDefaultValueSql("current_timestamp()")
                .HasColumnType("timestamp")
                .HasColumnName("played_at");
            entity.Property(e => e.SongId)
                .HasColumnType("int(11)")
                .HasColumnName("song_id");
            entity.Property(e => e.Votes)
                .HasColumnType("int(11)")
                .HasColumnName("votes");

            entity.HasOne(d => d.Break).WithMany(p => p.Histories)
                .HasForeignKey(d => d.BreakId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("history_ibfk_2");

            entity.HasOne(d => d.Song).WithMany(p => p.Histories)
                .HasForeignKey(d => d.SongId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("history_ibfk_1");
        });

        modelBuilder.Entity<Moderator>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("moderators");

            entity.HasIndex(e => e.UserId, "uniq_mod_user").IsUnique();

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.ArtistsBanned)
                .HasColumnType("int(11)")
                .HasColumnName("artists_banned");
            entity.Property(e => e.SongsApproved)
                .HasColumnType("int(11)")
                .HasColumnName("songs_approved");
            entity.Property(e => e.SongsBaned)
                .HasColumnType("int(11)")
                .HasColumnName("songs_baned");
            entity.Property(e => e.UserId)
                .HasColumnType("int(11)")
                .HasColumnName("user_id");

            entity.HasOne(d => d.User).WithOne(p => p.Moderator)
                .HasForeignKey<Moderator>(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("moderators_ibfk_1");
        });

        modelBuilder.Entity<QueueItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("queue_items");

            entity.HasIndex(e => e.BlacklistId, "blacklist_id");

            entity.HasIndex(e => e.BreakId, "break_id");

            entity.HasIndex(e => e.GreenlistId, "greenlist_id");

            entity.HasIndex(e => e.ModeratorId, "moderator_id");

            entity.HasIndex(e => e.SongId, "song_id");

            entity.HasIndex(e => new { e.BreakId, e.OrderIndex }, "uniq_break_order").IsUnique();

            entity.HasIndex(e => new { e.BreakId, e.SongId }, "uniq_break_song").IsUnique();

            entity.HasIndex(e => e.UserId, "user_id");

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.BlacklistId)
                .HasColumnType("int(11)")
                .HasColumnName("blacklist_id");
            entity.Property(e => e.BreakId)
                .HasColumnType("int(11)")
                .HasColumnName("break_id");
            entity.Property(e => e.GreenlistId)
                .HasColumnType("int(11)")
                .HasColumnName("greenlist_id");
            entity.Property(e => e.ModerationStatus)
                .HasColumnType("enum('approved','rejected')")
                .HasColumnName("moderation_status");
            entity.Property(e => e.ModeratorId)
                .HasColumnType("int(11)")
                .HasColumnName("moderator_id");
            entity.Property(e => e.OrderIndex)
                .HasColumnType("int(11)")
                .HasColumnName("order_index");
            entity.Property(e => e.SongId)
                .HasColumnType("int(11)")
                .HasColumnName("song_id");
            entity.Property(e => e.UserId)
                .HasColumnType("int(11)")
                .HasColumnName("user_id");

            entity.HasOne(d => d.Blacklist).WithMany(p => p.QueueItems)
                .HasForeignKey(d => d.BlacklistId)
                .HasConstraintName("queue_items_ibfk_5");

            entity.HasOne(d => d.Break).WithMany(p => p.QueueItems)
                .HasForeignKey(d => d.BreakId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("queue_items_ibfk_1");

            entity.HasOne(d => d.Greenlist).WithMany(p => p.QueueItems)
                .HasForeignKey(d => d.GreenlistId)
                .HasConstraintName("queue_items_ibfk_4");

            entity.HasOne(d => d.Moderator).WithMany(p => p.QueueItems)
                .HasForeignKey(d => d.ModeratorId)
                .HasConstraintName("queue_items_ibfk_6");

            entity.HasOne(d => d.Song).WithMany(p => p.QueueItems)
                .HasForeignKey(d => d.SongId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("queue_items_ibfk_2");

            entity.HasOne(d => d.User).WithMany(p => p.QueueItems)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("queue_items_ibfk_3");
        });

        modelBuilder.Entity<Rank>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("ranks");

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.Rank1)
                .HasMaxLength(255)
                .HasColumnName("rank");
        });

        modelBuilder.Entity<Song>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("songs");

            entity.HasIndex(e => e.ArtistId, "artist_id");

            entity.HasIndex(e => e.SpotifyId, "uniq_spotify_song").IsUnique();

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.ArtistId)
                .HasColumnType("int(11)")
                .HasColumnName("artist_id");
            entity.Property(e => e.Cover)
                .HasColumnType("text")
                .HasColumnName("cover");
            entity.Property(e => e.DurationMs)
                .HasColumnType("int(11)")
                .HasColumnName("duration_ms");
            entity.Property(e => e.SpotifyId).HasColumnName("spotify_id");
            entity.Property(e => e.Title)
                .HasColumnType("text")
                .HasColumnName("title");

            entity.HasOne(d => d.Artist).WithMany(p => p.Songs)
                .HasForeignKey(d => d.ArtistId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("songs_ibfk_1");
        });

        modelBuilder.Entity<SongBlacklist>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("song_blacklist");

            entity.HasIndex(e => e.ArtistId, "artist_id");

            entity.HasIndex(e => e.ModId, "mod_id");

            entity.HasIndex(e => e.SongId, "song_id").IsUnique();

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.ArtistId)
                .HasColumnType("int(11)")
                .HasColumnName("artist_id");
            entity.Property(e => e.ModId)
                .HasColumnType("int(11)")
                .HasColumnName("mod_id");
            entity.Property(e => e.SongId)
                .HasColumnType("int(11)")
                .HasColumnName("song_id");

            entity.HasOne(d => d.Artist).WithMany(p => p.SongBlacklists)
                .HasForeignKey(d => d.ArtistId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("song_blacklist_ibfk_2");

            entity.HasOne(d => d.Mod).WithMany(p => p.SongBlacklists)
                .HasForeignKey(d => d.ModId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("song_blacklist_ibfk_3");

            entity.HasOne(d => d.Song).WithOne(p => p.SongBlacklist)
                .HasForeignKey<SongBlacklist>(d => d.SongId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("song_blacklist_ibfk_1");
        });

        modelBuilder.Entity<SongGreenlist>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("song_greenlist");

            entity.HasIndex(e => e.ArtistId, "artist_id");

            entity.HasIndex(e => e.ModId, "mod_id");

            entity.HasIndex(e => e.SongId, "song_id").IsUnique();

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.ArtistId)
                .HasColumnType("int(11)")
                .HasColumnName("artist_id");
            entity.Property(e => e.ModId)
                .HasColumnType("int(11)")
                .HasColumnName("mod_id");
            entity.Property(e => e.SongId)
                .HasColumnType("int(11)")
                .HasColumnName("song_id");

            entity.HasOne(d => d.Artist).WithMany(p => p.SongGreenlists)
                .HasForeignKey(d => d.ArtistId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("song_greenlist_ibfk_2");

            entity.HasOne(d => d.Mod).WithMany(p => p.SongGreenlists)
                .HasForeignKey(d => d.ModId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("song_greenlist_ibfk_3");

            entity.HasOne(d => d.Song).WithOne(p => p.SongGreenlist)
                .HasForeignKey<SongGreenlist>(d => d.SongId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("song_greenlist_ibfk_1");
        });

        modelBuilder.Entity<SpecialEvent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("special_event");

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.EventStatus)
                .HasColumnType("enum('pending','playing','done')")
                .HasColumnName("event_status");
            entity.Property(e => e.FileUrl)
                .HasColumnType("text")
                .HasColumnName("file_url");
            entity.Property(e => e.StartsAt)
                .HasDefaultValueSql("current_timestamp()")
                .HasColumnType("timestamp")
                .HasColumnName("starts_at");
            entity.Property(e => e.Title)
                .HasMaxLength(255)
                .HasColumnName("title");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("users");

            entity.HasIndex(e => e.RankId, "rank_id");

            entity.HasIndex(e => e.UserLogin, "user_login").IsUnique();

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("current_timestamp()")
                .HasColumnType("datetime")
                .HasColumnName("created_at");
            entity.Property(e => e.PasswordChangedAt)
                .HasDefaultValueSql("current_timestamp()")
                .HasColumnType("datetime")
                .HasColumnName("password_changed_at");
            entity.Property(e => e.RankId)
                .HasDefaultValueSql("'1'")
                .HasColumnType("int(11)")
                .HasColumnName("rank_id");
            entity.Property(e => e.UserLogin).HasColumnName("user_login");
            entity.Property(e => e.UserPassword)
                .HasMaxLength(255)
                .HasColumnName("user_password");

            entity.HasOne(d => d.Rank).WithMany(p => p.Users)
                .HasForeignKey(d => d.RankId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("users_ibfk_1");

            entity.HasMany(d => d.Songs).WithMany(p => p.Users)
                .UsingEntity<Dictionary<string, object>>(
                    "UserFavourite",
                    r => r.HasOne<Song>().WithMany()
                        .HasForeignKey("SongId")
                        .OnDelete(DeleteBehavior.ClientSetNull)
                        .HasConstraintName("user_favourites_ibfk_2"),
                    l => l.HasOne<User>().WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.ClientSetNull)
                        .HasConstraintName("user_favourites_ibfk_1"),
                    j =>
                    {
                        j.HasKey("UserId", "SongId")
                            .HasName("PRIMARY")
                            .HasAnnotation("MySql:IndexPrefixLength", new[] { 0, 0 });
                        j.ToTable("user_favourites");
                        j.HasIndex(new[] { "SongId" }, "song_id");
                        j.IndexerProperty<int>("UserId")
                            .HasColumnType("int(11)")
                            .HasColumnName("user_id");
                        j.IndexerProperty<int>("SongId")
                            .HasColumnType("int(11)")
                            .HasColumnName("song_id");
                    });
        });

        modelBuilder.Entity<Vote>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("votes");

            entity.HasIndex(e => e.QueueItemId, "queue_item_id");

            entity.HasIndex(e => new { e.UserId, e.QueueItemId }, "user_id").IsUnique();

            entity.Property(e => e.Id)
                .HasColumnType("int(11)")
                .HasColumnName("id");
            entity.Property(e => e.QueueItemId)
                .HasColumnType("int(11)")
                .HasColumnName("queue_item_id");
            entity.Property(e => e.UserId)
                .HasColumnType("int(11)")
                .HasColumnName("user_id");

            entity.HasOne(d => d.QueueItem).WithMany(p => p.Votes)
                .HasForeignKey(d => d.QueueItemId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("votes_ibfk_2");

            entity.HasOne(d => d.User).WithMany(p => p.Votes)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("votes_ibfk_1");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
