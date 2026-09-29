using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("special_event")]
public partial class SpecialEvent
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("title")]
    [StringLength(255)]
    public string? Title { get; set; }

    [Column("file_url", TypeName = "text")]
    public string FileUrl { get; set; } = null!;

    [Column("starts_at", TypeName = "timestamp")]
    public DateTime StartsAt { get; set; }

    [Column("event_status", TypeName = "enum('pending','playing','done')")]
    public string EventStatus { get; set; } = null!;
}
