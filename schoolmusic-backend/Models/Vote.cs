using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace schoolmusic_backend.Models;

[Table("votes")]
[Index("QueueItemId", Name = "queue_item_id")]
[Index("UserId", "QueueItemId", Name = "user_id", IsUnique = true)]
public partial class Vote
{
    [Key]
    [Column("id", TypeName = "int(11)")]
    public int Id { get; set; }

    [Column("user_id", TypeName = "int(11)")]
    public int UserId { get; set; }

    [Column("queue_item_id", TypeName = "int(11)")]
    public int QueueItemId { get; set; }

    [ForeignKey("QueueItemId")]
    [InverseProperty("Votes")]
    public virtual QueueItem QueueItem { get; set; } = null!;

    [ForeignKey("UserId")]
    [InverseProperty("Votes")]
    public virtual User User { get; set; } = null!;
}
