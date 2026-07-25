using System;

namespace Jellyfin.Plugin.MyChannels.Models;

/// <summary>
/// A per-item override on a channel's scheduling behaviour. Keyed by the Jellyfin item id of a
/// top-level item within one of the channel's sources — a series, movie, season, collection,
/// music video, etc. Absent overrides default to weight 1 and block size 1. Stored sparsely
/// on <see cref="Channel.EntryOverrides"/>: only user-tweaked ids appear.
/// </summary>
public class EntryOverride
{
    /// <summary>Gets or sets the Jellyfin item id this override applies to.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets how many round-robin slots this item claims per rotation (1 = default). Higher weight = the item plays more often.</summary>
    public int? Weight { get; set; }

    /// <summary>Gets or sets the block size in episodes when this override applies to a series or season. Ignored for standalone items (movies, music videos, individual episodes). Null falls back to 1.</summary>
    public int? BlockSize { get; set; }
}
