using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("queue_items")]
[Index("BlacklistId", Name = "blacklist_id")]
[Index("BreakId", Name = "break_id")]
[Index("GreenlistId", Name = "greenlist_id")]
[Index("ModeratorId", Name = "moderator_id")]
[Index("SongId", Name = "song_id")]
[Index("BreakId", "OrderIndex", Name = "uniq_break_order", IsUnique = true)]
[Index("BreakId", "SongId", Name = "uniq_break_song", IsUnique = true)]
[Index("UserId", Name = "user_id")]
public partial class QueueItem
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("break_id", TypeName = "int(11)")]
    public int BreakId { get; set; }

    [Column("song_id", TypeName = "int(11)")]
    public int SongId { get; set; }

    [Column("user_id", TypeName = "int(11)")]
    public int UserId { get; set; }

    [Column("greenlist_id", TypeName = "int(11)")]
    public int? GreenlistId { get; set; }

    [Column("blacklist_id", TypeName = "int(11)")]
    public int? BlacklistId { get; set; }

    [Column("moderator_id", TypeName = "int(11)")]
    public int? ModeratorId { get; set; }

    [Column("moderation_status", TypeName = "enum('approved','rejected')")]
    public string? ModerationStatus { get; set; }

    [Column("order_index", TypeName = "int(11)")]
    public int OrderIndex { get; set; }

    [ForeignKey("BlacklistId")]
    [InverseProperty("QueueItems")]
    public virtual SongBlacklist? Blacklist { get; set; }

    [ForeignKey("BreakId")]
    [InverseProperty("QueueItems")]
    public virtual Break Break { get; set; } = null!;

    [ForeignKey("GreenlistId")]
    [InverseProperty("QueueItems")]
    public virtual SongGreenlist? Greenlist { get; set; }

    [ForeignKey("ModeratorId")]
    [InverseProperty("QueueItems")]
    public virtual Moderator? Moderator { get; set; }

    [ForeignKey("SongId")]
    [InverseProperty("QueueItems")]
    public virtual Song Song { get; set; } = null!;

    [ForeignKey("UserId")]
    [InverseProperty("QueueItems")]
    public virtual User User { get; set; } = null!;

    [InverseProperty("QueueItem")]
    public virtual ICollection<Vote> Votes { get; set; } = new List<Vote>();
}
