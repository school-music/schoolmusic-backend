using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class Vote
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public int QueueItemId { get; set; }

    public virtual QueueItem QueueItem { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
