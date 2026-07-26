using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MyChannels.Models;

namespace Jellyfin.Plugin.MyChannels.Services;

/// <summary>
/// Options controlling how a channel's resolved items are ordered into its looping schedule. Per-entry
/// weight and block size live on each <see cref="ProgramEntry"/> itself (populated at resolve time from
/// <see cref="Channel.EntryOverrides"/>); this struct only carries channel-wide ordering intent.
/// </summary>
/// <param name="KeepMultiPartTogether">Keep multi-part episodes adjacent and never split across a block.</param>
/// <param name="Mode">How block order is arranged: shuffle (deterministically), alphabetical by name, or chronological by date.</param>
/// <param name="ShuffleEpisodes">Shuffle episodes within a series instead of playing them in air order.</param>
/// <param name="ChannelId">Channel id, seeding the deterministic shuffle so the guide and stream agree.</param>
/// <param name="Rotation">A counter (e.g. days since an epoch) that advances which block each group contributes
/// first in a shuffled loop, so the channel walks through each series over time. Stable within a built schedule
/// (guide and stream agree); the caller bumps it each refresh.</param>
/// <param name="InterleaveOrder">How the shuffled loop orders its groups within each cycle: stable per channel every cycle (default), or shuffled per cycle so TNG/DS9/VOY on one cycle becomes DS9/VOY/TNG on the next.</param>
public readonly record struct ChannelLoopOptions(
    bool KeepMultiPartTogether,
    LoopMode Mode,
    bool ShuffleEpisodes,
    string ChannelId,
    int Rotation = 0,
    InterleaveOrder InterleaveOrder = InterleaveOrder.Same);

/// <summary>
/// Turns a channel's resolved items into the ordered loop it cycles through: items are grouped by
/// <see cref="ProgramEntry.TopLevelItemId"/> (each group shares a weight and block size derived from the
/// channel's per-entry overrides), episodes within a group are chunked into blocks, and blocks are ordered
/// for the channel. Pure and deterministic so the guide projection and the live stream always agree.
/// </summary>
public static class ProgramLoopBuilder
{
    // Trailing part markers: "Title (2)", "Title [2]", "Title - Part 2", "Title Pt. 2".
    private static readonly Regex PartSuffix = new(
        @"^(?<base>.*?)[\s:\-]*(?:\((?<n1>\d{1,2})\)|\[(?<n2>\d{1,2})\]|(?:part|pt\.?)\s*(?<n3>\d{1,2}))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Safety cap so an over-tuned config (e.g. all weights set to 1000) can't blow up allocations.
    private const int MaxWeightPerGroup = 100;

    /// <summary>
    /// Builds the ordered program loop.
    /// </summary>
    /// <param name="items">The channel's resolved items.</param>
    /// <param name="options">The ordering options.</param>
    /// <returns>The ordered loop.</returns>
    public static IReadOnlyList<ProgramEntry> Build(IReadOnlyList<ProgramEntry> items, ChannelLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return Array.Empty<ProgramEntry>();
        }

        var blocks = new List<Block>();

        // Group every entry by its top-level id — a series id for episodes, or the item's own id for movies /
        // music videos / loose videos. Each group shares a weight and block size (from any entry in it —
        // they were all populated from the same override at resolve time). Standalone items produce a group of
        // single-item blocks; episode groups get ordered, multi-part-glued, and chunked into blocks of the
        // group's block size.
        foreach (var group in items.GroupBy(i => i.TopLevelItemId))
        {
            var groupItems = group.ToList();
            var first = groupItems[0];
            var weight = Math.Clamp(first.Weight, 1, MaxWeightPerGroup);
            var blockSize = Math.Max(1, first.BlockSize);
            var groupKey = "group:" + group.Key.ToString("N");
            var sortName = first.SeriesName ?? first.Title ?? string.Empty;

            var episodes = groupItems.Where(e => e.SeriesId is not null).ToList();
            var standalones = groupItems.Where(e => e.SeriesId is null).ToList();

            foreach (var standalone in standalones)
            {
                blocks.Add(new Block(groupKey, standalone.Title ?? string.Empty, 0, weight, new List<ProgramEntry> { standalone }));
            }

            if (episodes.Count > 0)
            {
                var ordered = episodes
                    .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
                    .ThenBy(e => e.EpisodeNumber ?? int.MaxValue)
                    .ThenBy(e => e.RawName ?? e.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var units = GroupUnits(ordered, options.KeepMultiPartTogether);

                if (options.ShuffleEpisodes)
                {
                    var seriesKey = group.Key.ToString("N");
                    units = units
                        .OrderBy(u => ShuffleKey(options.ChannelId, seriesKey + ":" + u[0].ItemId.ToString("N")))
                        .ToList();
                }

                var seq = 0;
                var current = new List<ProgramEntry>();
                foreach (var unit in units)
                {
                    current.AddRange(unit);
                    if (current.Count >= blockSize)
                    {
                        blocks.Add(new Block(groupKey, sortName, seq++, weight, current));
                        current = new List<ProgramEntry>();
                    }
                }

                if (current.Count > 0)
                {
                    blocks.Add(new Block(groupKey, sortName, seq, weight, current));
                }
            }
        }

        if (blocks.Count == 0)
        {
            return Array.Empty<ProgramEntry>();
        }

        if (options.Mode != LoopMode.Shuffle)
        {
            // Chronological orders blocks by their earliest release/air date; alphabetical (the default non-shuffle
            // order) by title. Both fall back to name then sequence so the order is stable and series stay in order.
            var ordered = options.Mode == LoopMode.Chronological
                ? blocks.OrderBy(BlockDate).ThenBy(b => b.SortName, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Seq)
                : blocks.OrderBy(b => b.SortName, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Seq);

            return ordered.SelectMany(b => b.Items).ToList();
        }

        // Full-loop weighted round-robin. Every group contributes ALL its blocks per loop (shorter groups
        // wrap and repeat blocks when they run out). Each cycle deals each group's `Weight` slots consecutively;
        // the number of cycles = max over groups of ceil(blocks / weight) so the longest group airs its blocks
        // exactly once and shorter groups fill their slots by wrapping. Group order per cycle is either stable
        // per channel (InterleaveOrder.Same) or deterministically shuffled per cycle (InterleaveOrder.Shuffled).
        // The rotation counter advances the starting block within each group so successive refreshes walk
        // through the group instead of always beginning at block 0.
        var groups = blocks
            .GroupBy(b => b.GroupKey, StringComparer.Ordinal)
            .Select(g =>
            {
                var all = g.OrderBy(b => b.Seq).ToList();
                var weight = all[0].Weight;
                var offset = (int)((uint)ShuffleKey(options.ChannelId, "rot:" + g.Key) % (uint)all.Count);
                var start = (((options.Rotation + offset) % all.Count) + all.Count) % all.Count;
                return new GroupState(all, weight, start);
            })
            .OrderBy(g => ShuffleKey(options.ChannelId, "order:" + g.Blocks[0].GroupKey))
            .ToList();

        var maxCycles = groups.Max(g => (int)Math.Ceiling((double)g.Blocks.Count / g.Weight));

        var result = new List<ProgramEntry>();
        for (var cycle = 0; cycle < maxCycles; cycle++)
        {
            var cycleOrder = options.InterleaveOrder == InterleaveOrder.Shuffled
                ? ShuffleGroupsForCycle(groups, options.ChannelId, cycle)
                : groups;

            foreach (var g in cycleOrder)
            {
                for (var s = 0; s < g.Weight; s++)
                {
                    var idx = ((g.Start + (cycle * g.Weight) + s) % g.Blocks.Count + g.Blocks.Count) % g.Blocks.Count;
                    result.AddRange(g.Blocks[idx].Items);
                }
            }
        }

        return result;
    }

    // Deterministic per-cycle Fisher-Yates shuffle of the group order. Seed = channelId + cycle number, so
    // the guide projection and the live stream both compute the same order for any given cycle.
    private static List<GroupState> ShuffleGroupsForCycle(List<GroupState> stable, string channelId, int cycle)
    {
        var arr = stable.ToArray();
        var seed = ShuffleKey(channelId, "cycle:" + cycle);
        var rng = new Random(seed);
        for (var i = arr.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }

        return arr.ToList();
    }

    // Groups consecutive episodes that share a base title and a part marker into one unit, so a two-parter is kept
    // together (a block extends by at most one episode to hold the pair). A unit is capped at two episodes: a
    // three-parter (1)(2)(3) keeps (1)(2) together and lets (3) fall into the next block -- we never extend a block
    // by more than one. Every other episode is its own single-item unit.
    private static List<List<ProgramEntry>> GroupUnits(List<ProgramEntry> ordered, bool keepMultiPart)
    {
        var units = new List<List<ProgramEntry>>();
        if (!keepMultiPart)
        {
            foreach (var e in ordered)
            {
                units.Add(new List<ProgramEntry> { e });
            }

            return units;
        }

        List<ProgramEntry>? run = null;
        string? runBase = null;

        foreach (var e in ordered)
        {
            // Join the run only to complete a pair (cap at two episodes), so (1)(2) stay together but (3) does not.
            var partBase = MultiPartBase(e.RawName);
            if (partBase is not null && runBase is not null && run!.Count < 2 && string.Equals(partBase, runBase, StringComparison.OrdinalIgnoreCase))
            {
                run.Add(e);
                continue;
            }

            if (run is not null)
            {
                units.Add(run);
            }

            if (partBase is not null)
            {
                run = new List<ProgramEntry> { e };
                runBase = partBase;
            }
            else
            {
                units.Add(new List<ProgramEntry> { e });
                run = null;
                runBase = null;
            }
        }

        if (run is not null)
        {
            units.Add(run);
        }

        return units;
    }

    // Returns the title with its trailing part marker stripped (e.g. "The Trap (1)" -> "The Trap"), or null
    // when the name has no part marker. A short, non-empty base guards against false matches.
    private static string? MultiPartBase(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var match = PartSuffix.Match(name);
        if (!match.Success)
        {
            return null;
        }

        var baseTitle = match.Groups["base"].Value.Trim();
        return baseTitle.Length >= 2 ? baseTitle : null;
    }

    // A block's chronological key: the earliest release/air date across its items (a production-only year counts as
    // its 1 January), so undated content sorts last.
    private static DateTime BlockDate(Block block)
    {
        var earliest = DateTime.MaxValue;
        foreach (var item in block.Items)
        {
            var date = item.PremiereDate ?? (item.Year is int year ? new DateTime(year, 1, 1) : (DateTime?)null);
            if (date is { } value && value < earliest)
            {
                earliest = value;
            }
        }

        return earliest;
    }

    // FNV-1a hash of channel id + key, giving a stable per-channel ordering that survives restarts.
    private static int ShuffleKey(string channelId, string key)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in channelId)
            {
                hash = (hash ^ (byte)c) * 16777619u;
            }

            foreach (var c in key)
            {
                hash = (hash ^ (byte)c) * 16777619u;
            }

            return (int)hash;
        }
    }

    private sealed record Block(string GroupKey, string SortName, int Seq, int Weight, List<ProgramEntry> Items);

    // Per-group state carried through the round-robin: its blocks (ordered by Seq), the number of slots it
    // deals per cycle (Weight), and the rotation-shifted starting block index.
    private sealed record GroupState(List<Block> Blocks, int Weight, int Start);
}
