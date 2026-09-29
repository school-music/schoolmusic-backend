using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("artist_blacklist")]
[Index("ArtistId", Name = "artist_id", IsUnique = true)]
[Index("ModeratorId", Name = "moderator_id")]
public partial class ArtistBlacklist
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("artist_id", TypeName = "int(11)")]
    public int ArtistId { get; set; }

    [Column("moderator_id", TypeName = "int(11)")]
    public int ModeratorId { get; set; }

    [ForeignKey("ArtistId")]
    [InverseProperty("ArtistBlacklist")]
    public virtual Artist Artist { get; set; } = null!;

    [ForeignKey("ModeratorId")]
    [InverseProperty("ArtistBlacklists")]
    public virtual Moderator Moderator { get; set; } = null!;
}
