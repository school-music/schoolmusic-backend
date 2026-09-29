using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("ranks")]
public partial class Rank
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("rank")]
    [StringLength(255)]
    public string RankName { get; set; } = null!;

    [InverseProperty("Rank")]
    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
