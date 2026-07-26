namespace Jellyfin.Plugin.MyChannels.Models;

/// <summary>
/// How the shuffled loop orders its groups within each round-robin cycle. A "cycle" deals every group's
/// per-cycle slot count (its <see cref="EntryOverride.Weight"/>) once; the channel then advances to the
/// next cycle until every group has aired all its blocks (shorter groups wrap their blocks to fill).
/// </summary>
public enum InterleaveOrder
{
    /// <summary>Deal groups in a stable per-channel order every cycle (TNG, DS9, VOY, TNG, DS9, VOY, …).</summary>
    Same = 0,

    /// <summary>Deterministically shuffle the group order per cycle (TNG, DS9, VOY, then DS9, VOY, TNG, …).</summary>
    Shuffled = 1
}
