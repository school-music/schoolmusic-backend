using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Pomelo.EntityFrameworkCore.MySql.Scaffolding.Internal;

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
            .UseCollation("utf8mb4_general_ci")
            .HasCharSet("utf8mb4");

        modelBuilder.Entity<Artist>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");
        });

        modelBuilder.Entity<ArtistBlacklist>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasOne(d => d.Artist).WithOne(p => p.ArtistBlacklist)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("artist_blacklist_ibfk_1");

            entity.HasOne(d => d.Moderator).WithMany(p => p.ArtistBlacklists)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("artist_blacklist_ibfk_2");
        });

        modelBuilder.Entity<BannedUser>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.Property(e => e.BannedAt).HasDefaultValueSql("current_timestamp()");

            entity.HasOne(d => d.Moderator).WithMany(p => p.BannedUsers)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("banned_users_ibfk_2");

            entity.HasOne(d => d.User).WithMany(p => p.BannedUsers)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("banned_users_ibfk_1");
        });

        modelBuilder.Entity<Break>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.Property(e => e.StartAt).HasDefaultValueSql("current_timestamp()");
        });

        modelBuilder.Entity<DefaultSong>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasOne(d => d.Song).WithMany(p => p.DefaultSongs)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("default_songs_ibfk_1");
        });

        modelBuilder.Entity<ExceptionDay>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.Property(e => e.StartAt).HasDefaultValueSql("current_timestamp()");
        });

        modelBuilder.Entity<History>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.Property(e => e.PlayedAt).HasDefaultValueSql("current_timestamp()");

            entity.HasOne(d => d.Break).WithMany(p => p.Histories)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("history_ibfk_2");

            entity.HasOne(d => d.Song).WithMany(p => p.Histories)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("history_ibfk_1");
        });

        modelBuilder.Entity<Moderator>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasOne(d => d.User).WithOne(p => p.Moderator)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("moderators_ibfk_1");
        });

        modelBuilder.Entity<QueueItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasOne(d => d.Blacklist).WithMany(p => p.QueueItems).HasConstraintName("queue_items_ibfk_5");

            entity.HasOne(d => d.Break).WithMany(p => p.QueueItems)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("queue_items_ibfk_1");

            entity.HasOne(d => d.Greenlist).WithMany(p => p.QueueItems).HasConstraintName("queue_items_ibfk_4");

            entity.HasOne(d => d.Moderator).WithMany(p => p.QueueItems).HasConstraintName("queue_items_ibfk_6");

            entity.HasOne(d => d.Song).WithMany(p => p.QueueItems)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("queue_items_ibfk_2");

            entity.HasOne(d => d.User).WithMany(p => p.QueueItems)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("queue_items_ibfk_3");
        });

        modelBuilder.Entity<Rank>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");
        });

        modelBuilder.Entity<Song>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasOne(d => d.Artist).WithMany(p => p.Songs)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("songs_ibfk_1");
        });

        modelBuilder.Entity<SongBlacklist>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasOne(d => d.Artist).WithMany(p => p.SongBlacklists)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("song_blacklist_ibfk_2");

            entity.HasOne(d => d.Mod).WithMany(p => p.SongBlacklists)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("song_blacklist_ibfk_3");

            entity.HasOne(d => d.Song).WithOne(p => p.SongBlacklist)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("song_blacklist_ibfk_1");
        });

        modelBuilder.Entity<SongGreenlist>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.HasOne(d => d.Artist).WithMany(p => p.SongGreenlists)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("song_greenlist_ibfk_2");

            entity.HasOne(d => d.Mod).WithMany(p => p.SongGreenlists)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("song_greenlist_ibfk_3");

            entity.HasOne(d => d.Song).WithOne(p => p.SongGreenlist)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("song_greenlist_ibfk_1");
        });

        modelBuilder.Entity<SpecialEvent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

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

            entity.HasOne(d => d.QueueItem).WithMany(p => p.Votes)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("votes_ibfk_2");

            entity.HasOne(d => d.User).WithMany(p => p.Votes)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("votes_ibfk_1");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
