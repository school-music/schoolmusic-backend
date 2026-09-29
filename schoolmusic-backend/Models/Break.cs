using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("break")]
public partial class Break
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("start_at", TypeName = "timestamp")]
    public DateTime StartAt { get; set; }

    [Column("ends_at", TypeName = "timestamp")]
    public DateTime? EndsAt { get; set; }

    [Column("vote_ends", TypeName = "timestamp")]
    public DateTime? VoteEnds { get; set; }

    [Column("break_number", TypeName = "int(11)")]
    public int? BreakNumber { get; set; }

    [InverseProperty("Break")]
    public virtual ICollection<History> Histories { get; set; } = new List<History>();

    [InverseProperty("Break")]
    public virtual ICollection<QueueItem> QueueItems { get; set; } = new List<QueueItem>();
}
