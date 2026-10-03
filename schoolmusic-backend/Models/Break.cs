using System;
using System.Collections.Generic;

namespace schoolmusic_backend.Models;

public partial class Break
{
    public int Id { get; set; }

    public DateTime StartAt { get; set; }

    public DateTime? EndsAt { get; set; }

    public DateTime? VoteEnds { get; set; }

    public int? BreakNumber { get; set; }

    public virtual ICollection<History> Histories { get; set; } = new List<History>();

    public virtual ICollection<QueueItem> QueueItems { get; set; } = new List<QueueItem>();
}
