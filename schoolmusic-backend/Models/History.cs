using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("history")]
[Index("BreakId", Name = "break_id")]
[Index("SongId", Name = "song_id")]
public partial class History
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("song_id", TypeName = "int(11)")]
    public int SongId { get; set; }

    [Column("played_at", TypeName = "timestamp")]
    public DateTime PlayedAt { get; set; }

    [Column("break_id", TypeName = "int(11)")]
    public int BreakId { get; set; }

    [Column("votes", TypeName = "int(11)")]
    public int Votes { get; set; }

    [ForeignKey("BreakId")]
    [InverseProperty("Histories")]
    public virtual Break Break { get; set; } = null!;

    [ForeignKey("SongId")]
    [InverseProperty("Histories")]
    public virtual Song Song { get; set; } = null!;
}
