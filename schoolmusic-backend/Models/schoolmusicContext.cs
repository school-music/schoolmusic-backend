using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microting.EntityFrameworkCore.MySql.Scaffolding.Internal;

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

    public virtual DbSet<BreakTemplate> BreakTemplates { get; set; }

    public virtual DbSet<DefaultSong> DefaultSongs { get; set; }

    public virtual DbSet<ExceptionBreak> ExceptionBreaks { get; set; }

    public virtual DbSet<ExceptionDay> ExceptionDays { get; set; }

    public virtual DbSet<History> Histories { get; set; }

    public virtual DbSet<Moderator> Moderators { get; set; }

    public virtual DbSet<PlaybackState> PlaybackStates { get; set; }

    public virtual DbSet<SpotifyPlaybackConnection> SpotifyPlaybackConnections { get; set; }

    public virtual DbSet<QueueItem> QueueItems { get; set; }

    public virtual DbSet<Rank> Ranks { get; set; }

    public virtual DbSet<Song> Songs { get; set; }

    public virtual DbSet<SongBlacklist> SongBlacklists { get; set; }

    public virtual DbSet<SongGreenlist> SongGreenlists { get; set; }

    public virtual DbSet<SongVoteCount> SongVoteCounts { get; set; }

    public virtual DbSet<SpecialEvent> SpecialEvents { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<Vote> Votes { get; set; }

    public virtual DbSet<WeeklyBreakSetting> WeeklyBreakSettings { get; set; }

    public virtual DbSet<WeeklySchedule> WeeklySchedules { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .UseCollation("utf8mb4_polish_ci")
            .HasCharSet("utf8mb4");

        modelBuilder.Entity<Artist>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasIndex(e => e.Name, "idx_artists_name").HasAnnotation("MySql:IndexPrefixLength", new[] { 191 });

            entity.Property(e => e.Source).HasDefaultValueSql("'spotify'");
        });

        modelBuilder.Entity<ArtistBlacklist>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasOne(d => d.Artist).WithOne(p => p.ArtistBlacklist)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_artist_black_artist");

            entity.HasOne(d => d.Moderator).WithMany(p => p.ArtistBlacklists)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_artist_black_mod");
        });

        modelBuilder.Entity<BannedUser>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.Property(e => e.BannedAt).HasDefaultValueSql("current_timestamp()");

            entity.HasOne(d => d.Moderator).WithMany(p => p.BannedUsers)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_banned_mod");

            entity.HasOne(d => d.User).WithMany(p => p.BannedUsers)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_banned_user");
        });

        modelBuilder.Entity<Break>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.Property(e => e.MusicEnabled).HasDefaultValue(true);
        });

        modelBuilder.Entity<BreakTemplate>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");
        });

        modelBuilder.Entity<DefaultSong>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasOne(d => d.Song).WithMany(p => p.DefaultSongs)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_default_song");
        });

        modelBuilder.Entity<ExceptionBreak>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");
        });

        modelBuilder.Entity<ExceptionDay>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");
        });

        modelBuilder.Entity<History>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.Property(e => e.PlayedAt).HasDefaultValueSql("current_timestamp()");

            entity.HasOne(d => d.Break).WithMany(p => p.Histories)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_history_break");

            entity.HasOne(d => d.Song).WithMany(p => p.Histories)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_history_song");
        });

        modelBuilder.Entity<Moderator>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasOne(d => d.User).WithOne(p => p.Moderator)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_moderators_user");
        });

        modelBuilder.Entity<PlaybackState>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.Property(e => e.Id)
                .HasColumnType("tinyint(4)")
                .HasDefaultValueSql("'1'");
            entity.Property(e => e.UpdatedAt)
                .ValueGeneratedOnAddOrUpdate()
                .HasDefaultValueSql("current_timestamp()");

            entity.HasOne(d => d.CurrentQueueItem).WithMany(p => p.PlaybackStates)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("fk_playback_queue");
        });

        modelBuilder.Entity<SpotifyPlaybackConnection>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");
            entity.ToTable("spotify_playback_connection", table => table.HasCheckConstraint("chk_spotify_playback_singleton", "id = 1"));
        });

        modelBuilder.Entity<QueueItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("current_timestamp()");
            entity.Property(e => e.Status).HasDefaultValueSql("'queued'");

            entity.HasOne(d => d.Blacklist).WithMany(p => p.QueueItems).HasConstraintName("fk_queue_black");

            entity.HasOne(d => d.Break).WithMany(p => p.QueueItems)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_queue_break");

            entity.HasOne(d => d.Greenlist).WithMany(p => p.QueueItems).HasConstraintName("fk_queue_green");

            entity.HasOne(d => d.Moderator).WithMany(p => p.QueueItems).HasConstraintName("fk_queue_mod");

            entity.HasOne(d => d.Song).WithMany(p => p.QueueItems)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_queue_song");

            entity.HasOne(d => d.User).WithMany(p => p.QueueItems).HasConstraintName("fk_queue_user");
        });

        modelBuilder.Entity<Rank>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");
        });

        modelBuilder.Entity<Song>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("current_timestamp()");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Source).HasDefaultValueSql("'spotify'");

            entity.HasOne(d => d.Artist).WithMany(p => p.Songs)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_songs_artist");
        });

        modelBuilder.Entity<SongBlacklist>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasOne(d => d.Artist).WithMany(p => p.SongBlacklists)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_black_artist");

            entity.HasOne(d => d.Mod).WithMany(p => p.SongBlacklists)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_black_mod");

            entity.HasOne(d => d.Song).WithOne(p => p.SongBlacklist)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_black_song");
        });

        modelBuilder.Entity<SongGreenlist>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasOne(d => d.Artist).WithMany(p => p.SongGreenlists)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_green_artist");

            entity.HasOne(d => d.Mod).WithMany(p => p.SongGreenlists)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_green_mod");

            entity.HasOne(d => d.Song).WithOne(p => p.SongGreenlist)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_green_song");
        });

        modelBuilder.Entity<SongVoteCount>(entity =>
        {
            entity.ToView("song_vote_counts");
        });

        modelBuilder.Entity<SpecialEvent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.Property(e => e.EventStatus).HasDefaultValueSql("'pending'");
            entity.Property(e => e.StartsAt).HasDefaultValueSql("current_timestamp()");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("current_timestamp()");
            entity.Property(e => e.PasswordChangedAt).HasDefaultValueSql("current_timestamp()");
            entity.Property(e => e.RankId).HasDefaultValueSql("'1'");

            entity.HasOne(d => d.Rank).WithMany(p => p.Users)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_users_rank");

            entity.HasMany(d => d.Songs).WithMany(p => p.Users)
                .UsingEntity<Dictionary<string, object>>(
                    "UserFavourite",
                    r => r.HasOne<Song>().WithMany()
                        .HasForeignKey("SongId")
                        .OnDelete(DeleteBehavior.ClientSetNull)
                        .HasConstraintName("fk_fav_song"),
                    l => l.HasOne<User>().WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.ClientSetNull)
                        .HasConstraintName("fk_fav_user"),
                    j =>
                    {
                        j.HasKey("UserId", "SongId")
                            .HasName("PRIMARY")
                            .HasAnnotation("MySql:IndexPrefixLength", new[] { 0, 0 });
                        j.ToTable("user_favourites");
                        j.HasIndex(new[] { "SongId" }, "idx_fav_song");
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

            entity.Property(e => e.VotedAt).HasDefaultValueSql("current_timestamp()");

            entity.HasOne(d => d.QueueItem).WithMany(p => p.Votes)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_votes_queue");

            entity.HasOne(d => d.User).WithMany(p => p.Votes)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_votes_user");
        });

        modelBuilder.Entity<WeeklyBreakSetting>(entity =>
        {
            entity.HasKey(e => new { e.Weekday, e.BreakNumber })
                .HasName("PRIMARY")
                .HasAnnotation("MySql:IndexPrefixLength", new[] { 0, 0 });

            entity.Property(e => e.Weekday).HasColumnType("tinyint(4)");

            entity.HasOne(d => d.BreakNumberNavigation).WithMany(p => p.WeeklyBreakSettings)
                .HasPrincipalKey(p => p.BreakNumber)
                .HasForeignKey(d => d.BreakNumber)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_wbs_break");

            entity.HasOne(d => d.WeekdayNavigation).WithMany(p => p.WeeklyBreakSettings)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_wbs_weekday");
        });

        modelBuilder.Entity<WeeklySchedule>(entity =>
        {
            entity.HasKey(e => e.Weekday).HasName("PRIMARY");

            entity.Property(e => e.Weekday).HasColumnType("tinyint(4)");

            entity.Property(e => e.MusicEnabled).HasDefaultValue(true);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
