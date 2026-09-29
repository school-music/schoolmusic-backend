using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("exception_day")]
public partial class ExceptionDay
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("start_at", TypeName = "timestamp")]
    public DateTime StartAt { get; set; }

    [Column("ends_at", TypeName = "timestamp")]
    public DateTime? EndsAt { get; set; }

    [Column("description", TypeName = "text")]
    public string? Description { get; set; }
}
