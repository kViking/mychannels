using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MyChannels.Models;
using Jellyfin.Plugin.MyChannels.Services;
using Xunit;

namespace Jellyfin.Plugin.MyChannels.Tests;

/// <summary>
/// Tests for <see cref="ProgramLoopBuilder"/> — block grouping, multi-part keeping, deterministic order,
/// and per-entry Weight/BlockSize (v1.1.0.0).
/// </summary>
public class LoopBuilderTests
{
    private static readonly long Hour = TimeSpan.FromHours(1).Ticks;

    // A movie: its own top-level id. Weight/BlockSize default to 1.
    private static ProgramEntry Movie(string title, int weight = 1)
    {
        var id = Guid.NewGuid();
        return new ProgramEntry(id, title, null, Hour, "/m.mkv")
        {
            IsMovie = true,
            TopLevelItemId = id,
            Weight = weight,
            BlockSize = 1
        };
    }

    // An episode of a series: TopLevelItemId = seriesId (all episodes of a series share this so they group).
    private static ProgramEntry Ep(Guid seriesId, string seriesName, int season, int number, string rawName, int weight = 1, int blockSize = 1)
        => new ProgramEntry(Guid.NewGuid(), seriesName + " - " + rawName, null, Hour, "/e.mkv")
        {
            SeriesId = seriesId,
            SeriesName = seriesName,
            SeasonNumber = season,
            EpisodeNumber = number,
            RawName = rawName,
            TopLevelItemId = seriesId,
            Weight = weight,
            BlockSize = blockSize
        };

    // Shorthand for building options. Block size and weight now live on entries; options only carries channel-wide
    // ordering intent (mode, keep-multi-part, per-episode shuffle, channel id for seed, rotation counter,
    // interleave order).
    private static ChannelLoopOptions Opts(bool keepMulti = true, LoopMode mode = LoopMode.Alphabetical, bool shuffleEp = false, string ch = "ch1", int rotation = 0, InterleaveOrder interleave = InterleaveOrder.Same)
        => new ChannelLoopOptions(keepMulti, mode, shuffleEp, ch, rotation, interleave);

    // Episode numbers of one series, in output order.
    private static List<int> EpisodeOrder(IReadOnlyList<ProgramEntry> loop, Guid seriesId)
        => loop.Where(e => e.SeriesId == seriesId).Select(e => e.EpisodeNumber ?? -1).ToList();

    // Asserts a series' episodes occupy a contiguous run of the loop in the given episode order.
    private static void AssertContiguous(IReadOnlyList<ProgramEntry> loop, Guid seriesId, int[] expected)
    {
        var indices = new List<int>();
        for (var i = 0; i < loop.Count; i++)
        {
            if (loop[i].SeriesId == seriesId)
            {
                indices.Add(i);
            }
        }

        Assert.Equal(expected.Length, indices.Count);
        Assert.Equal(indices.Count - 1, indices[^1] - indices[0]); // contiguous
        Assert.Equal(expected, EpisodeOrder(loop, seriesId).ToArray());
    }

    // The longest run of consecutive items sharing a series.
    private static int MaxRun(IReadOnlyList<ProgramEntry> loop)
    {
        var maxRun = loop.Count > 0 ? 1 : 0;
        var run = 1;
        for (var i = 1; i < loop.Count; i++)
        {
            run = loop[i].SeriesId == loop[i - 1].SeriesId ? run + 1 : 1;
            maxRun = Math.Max(maxRun, run);
        }

        return maxRun;
    }

    [Fact]
    public void Empty_ReturnsEmpty()
        => Assert.Empty(ProgramLoopBuilder.Build(Array.Empty<ProgramEntry>(), Opts()));

    [Fact]
    public void Episodes_PlayInAirOrder()
    {
        var s = Guid.NewGuid();
        var loop = ProgramLoopBuilder.Build(new[]
        {
            Ep(s, "Show", 1, 3, "C"), Ep(s, "Show", 1, 1, "A"), Ep(s, "Show", 1, 2, "B")
        }, Opts());

        Assert.Equal(new[] { 1, 2, 3 }, EpisodeOrder(loop, s).ToArray());
    }

    [Fact]
    public void Blocks_KeepSeriesContiguousAndInOrder_WhenShuffled()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var items = new List<ProgramEntry>();
        for (var i = 1; i <= 4; i++)
        {
            items.Add(Ep(a, "Alpha", 1, i, "a" + i, blockSize: 4));
            items.Add(Ep(b, "Bravo", 1, i, "b" + i, blockSize: 4));
        }

        // Block size = series length, shuffled: each series is one block, so its episodes stay contiguous.
        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        Assert.Equal(8, loop.Count);
        AssertContiguous(loop, a, new[] { 1, 2, 3, 4 });
        AssertContiguous(loop, b, new[] { 1, 2, 3, 4 });
    }

    [Fact]
    public void MultiPart_StaysAdjacent_EvenAtBlockSizeOne()
    {
        var s = Guid.NewGuid();
        var loop = ProgramLoopBuilder.Build(new[]
        {
            Ep(s, "Show", 1, 1, "The Trap (1)"),
            Ep(s, "Show", 1, 2, "The Trap (2)")
        }, Opts(mode: LoopMode.Shuffle));

        Assert.Equal(new[] { 1, 2 }, EpisodeOrder(loop, s).ToArray());
    }

    [Fact]
    public void MultiPart_BlockExtendsByAtMostOne_ThirdPartNotGlued()
    {
        // A three-parter (1)(2)(3) with block size 2 must split into a pair block [1,2] and a singleton [3].
        // Under full-loop semantics both blocks air, so total is 3 items; the invariant is that (1)(2) stay
        // adjacent in the pair block and (3) is in its own block (not extending the pair to 3).
        var s = Guid.NewGuid();
        var loop = ProgramLoopBuilder.Build(new[]
        {
            Ep(s, "Show", 1, 1, "The Saga (1)", blockSize: 2),
            Ep(s, "Show", 1, 2, "The Saga (2)", blockSize: 2),
            Ep(s, "Show", 1, 3, "The Saga (3)", blockSize: 2)
        }, Opts(mode: LoopMode.Shuffle));

        Assert.Equal(3, loop.Count);
        var order = loop.Select(e => e.EpisodeNumber).ToList();
        var idx1 = order.IndexOf(1);
        var idx2 = order.IndexOf(2);
        Assert.True(idx1 >= 0 && idx2 == idx1 + 1,
            "(1) and (2) must be adjacent (pair kept together): " + string.Join(",", order));
    }

    [Fact]
    public void MultiPart_StaysAdjacent_WhenEpisodesShuffled()
    {
        var s = Guid.NewGuid();
        var loop = ProgramLoopBuilder.Build(new[]
        {
            Ep(s, "Show", 1, 1, "One"),
            Ep(s, "Show", 1, 2, "Big Story - Part 1"),
            Ep(s, "Show", 1, 3, "Big Story - Part 2"),
            Ep(s, "Show", 1, 4, "Four"),
            Ep(s, "Show", 1, 5, "Five")
        }, Opts(shuffleEp: true));

        var order = EpisodeOrder(loop, s);
        Assert.Equal(order.IndexOf(2) + 1, order.IndexOf(3));
    }

    [Fact]
    public void Shuffle_IsDeterministic()
    {
        var s = Guid.NewGuid();
        var items = Enumerable.Range(1, 6).Select(i => Ep(s, "Show", 1, i, "e" + i, blockSize: 2)).ToList();

        var a = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));
        var b = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        Assert.Equal(a.Select(e => e.ItemId), b.Select(e => e.ItemId));
    }

    [Fact]
    public void Shuffle_EqualSeries_RoundRobin_NoBackToBackBlocks()
    {
        // Five equal-sized series, 4-episode blocks. Round-robin must interleave them perfectly, so no series ever
        // plays two blocks back to back -- the longest same-series run is a single block (4 episodes).
        var items = new List<ProgramEntry>();
        for (var s = 0; s < 5; s++)
        {
            var id = new Guid("2222222" + s + "-2222-2222-2222-222222222222");
            items.AddRange(Enumerable.Range(1, 20).Select(i => Ep(id, "Show" + s, 1, i, "e" + i, blockSize: 4)));
        }

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        Assert.True(MaxRun(loop) <= 4, "a series ran longer than one 4-episode block: " + MaxRun(loop));
    }

    [Fact]
    public void Shuffle_DefaultWeights_LoopExhaustsAllBlocks_ShorterGroupsWrap()
    {
        // Under default weight=1, one loop = enough cycles for the longest group's blocks to all air once.
        // Big series (10 blocks) sets maxCycles = 10; each smaller series (5 blocks) wraps to fill 10 slots.
        // Total = 5 groups × 10 slots × 4 eps = 200 items. No group ever plays two blocks back to back within
        // a cycle (MaxRun capped at blockSize).
        var items = new List<ProgramEntry>();
        var big = new Guid("11111111-1111-1111-1111-111111111111");
        items.AddRange(Enumerable.Range(1, 40).Select(i => Ep(big, "Futurama", 1, i, "e" + i, blockSize: 4)));
        for (var s = 0; s < 4; s++)
        {
            var id = new Guid("2222222" + s + "-2222-2222-2222-222222222222");
            items.AddRange(Enumerable.Range(1, 20).Select(i => Ep(id, "Show" + s, 1, i, "e" + i, blockSize: 4)));
        }

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        Assert.Equal(200, loop.Count);
        Assert.Equal(40, loop.Count(e => e.SeriesId == big));       // big series airs all 40 eps once
        Assert.Equal(40, loop.Count(e => e.SeriesId == new Guid("22222220-2222-2222-2222-222222222222"))); // small wraps to 40 (2x each block)
        Assert.True(MaxRun(loop) <= 4, "a series ran longer than one block within a cycle: " + MaxRun(loop));
    }

    [Fact]
    public void PerEntryWeight_MultipliesSlotsPerCycle()
    {
        // Three series (10 blocks each, blockSize=1) with weights 3, 2, 1. maxCycles = max(ceil(10/3), ceil(10/2),
        // ceil(10/1)) = 10. Each group's total slots = weight × maxCycles: A=30, B=20, C=10. Ratio 3:2:1.
        var a = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var b = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var c = new Guid("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var items = new List<ProgramEntry>();
        items.AddRange(Enumerable.Range(1, 10).Select(i => Ep(a, "Alpha", 1, i, "a" + i, weight: 3, blockSize: 1)));
        items.AddRange(Enumerable.Range(1, 10).Select(i => Ep(b, "Bravo", 1, i, "b" + i, weight: 2, blockSize: 1)));
        items.AddRange(Enumerable.Range(1, 10).Select(i => Ep(c, "Charlie", 1, i, "c" + i, weight: 1, blockSize: 1)));

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        Assert.Equal(30, loop.Count(e => e.SeriesId == a));
        Assert.Equal(20, loop.Count(e => e.SeriesId == b));
        Assert.Equal(10, loop.Count(e => e.SeriesId == c));
    }

    [Fact]
    public void PerEntryBlockSize_DifferentiatesBlockSizes()
    {
        // Series A with blockSize=3 packs 3 episodes per block → 2 blocks; series B with blockSize=1 packs one →
        // 6 blocks. Weight=1 both. maxCycles = max(2, 6) = 6 cycles. A slots = 1×6 = 6, wrapping through its 2
        // blocks (each block airs 3×), giving 6 × 3 eps = 18 eps of A. B slots = 1×6 = 6 blocks × 1 ep = 6 eps.
        var a = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var b = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var items = new List<ProgramEntry>();
        items.AddRange(Enumerable.Range(1, 6).Select(i => Ep(a, "Alpha", 1, i, "a" + i, blockSize: 3)));
        items.AddRange(Enumerable.Range(1, 6).Select(i => Ep(b, "Bravo", 1, i, "b" + i, blockSize: 1)));

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        Assert.Equal(18, loop.Count(e => e.SeriesId == a));
        Assert.Equal(6, loop.Count(e => e.SeriesId == b));
    }

    [Fact]
    public void Overrides_MissingItemDefaultsToOneOne()
    {
        // Movies constructed without explicit weight/blockSize should behave like weight=1, blockSize=1: 3 movies
        // → each appears once per loop.
        var loop = ProgramLoopBuilder.Build(
            new[] { Movie("Alpha"), Movie("Bravo"), Movie("Charlie") },
            Opts(mode: LoopMode.Shuffle));

        Assert.Equal(3, loop.Count);
    }

    [Fact]
    public void Overrides_SharedTopLevelSharesWeight()
    {
        // Two episodes of one series share TopLevelItemId (the series id) so they inherit the same weight and
        // block size as one group. Series has 2 blocks, weight=4 → ceil(2/4) = 1 cycle. Movie: 1 block, weight=1
        // → ceil(1/1) = 1 cycle. maxCycles = 1. Series slots = 4×1 = 4 (wraps through its 2 blocks 2×). Movie
        // slot = 1. Total = 5 items.
        var s = Guid.NewGuid();
        var items = new[]
        {
            Ep(s, "Show", 1, 1, "one", weight: 4, blockSize: 1),
            Ep(s, "Show", 1, 2, "two", weight: 4, blockSize: 1),
            Movie("Solo") // weight=1
        };

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        Assert.Equal(5, loop.Count);
        Assert.Equal(4, loop.Count(e => e.SeriesId == s));
    }

    [Fact]
    public void PerEntryWeight_MoviesFavored_OutnumberSeries()
    {
        // Four movies at weight=6 (each = 1 block) against a 4-ep series at weight=1 blockSize=1 (4 blocks).
        // maxCycles = max(ceil(1/6)=1, ceil(4/1)=4) = 4. Movies each get 6×4 = 24 slots (wraps 24× through
        // their single block). Series gets 1×4 = 4 slots. Movies dominate 24 : 1 per movie.
        var items = new List<ProgramEntry>();
        for (var i = 0; i < 4; i++)
        {
            items.Add(Movie("Movie" + i, weight: 6));
        }

        var show = new Guid("33333333-3333-3333-3333-333333333333");
        items.AddRange(Enumerable.Range(1, 4).Select(i => Ep(show, "Show", 1, i, "e" + i, weight: 1, blockSize: 1)));

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        var movieSlots = loop.Count(e => e.SeriesId is null);
        var showSlots = loop.Count(e => e.SeriesId == show);
        Assert.Equal(4 * 6 * 4, movieSlots);      // 4 movies × weight 6 × 4 cycles = 96 slots
        Assert.Equal(4, showSlots);               // series 1×4 = 4 slots
        Assert.True(movieSlots > showSlots);
    }

    [Fact]
    public void Shuffle_SeriesBlockRotatesWithRotationCounter()
    {
        // The single block a series contributes advances with the rotation counter, so the channel works through
        // the series over successive refreshes instead of replaying the same episodes forever.
        var s = Guid.NewGuid();
        var items = Enumerable.Range(1, 12).Select(i => Ep(s, "Show", 1, i, "e" + i, blockSize: 4)).ToList();

        var day0 = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle, rotation: 0));
        var day1 = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle, rotation: 1));

        Assert.NotEqual(day0.Select(e => e.EpisodeNumber), day1.Select(e => e.EpisodeNumber));
    }

    [Fact]
    public void Movies_OrderAlphabetically_WhenNotShuffled()
    {
        var loop = ProgramLoopBuilder.Build(new[] { Movie("Zed"), Movie("Abe"), Movie("Mid") }, Opts());
        Assert.Equal(new[] { "Abe", "Mid", "Zed" }, loop.Select(e => e.Title).ToArray());
    }

    [Fact]
    public void Chronological_OrdersBlocksByDateOldestFirst()
    {
        var newId = Guid.NewGuid();
        var oldId = Guid.NewGuid();
        var midId = Guid.NewGuid();
        var items = new[]
        {
            new ProgramEntry(newId, "New", null, Hour, "/m.mkv") { IsMovie = true, PremiereDate = new DateTime(2020, 6, 1), TopLevelItemId = newId },
            new ProgramEntry(oldId, "Old", null, Hour, "/m.mkv") { IsMovie = true, PremiereDate = new DateTime(1999, 6, 1), TopLevelItemId = oldId },
            new ProgramEntry(midId, "Mid", null, Hour, "/m.mkv") { IsMovie = true, Year = 2010, TopLevelItemId = midId }
        };

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Chronological));
        Assert.Equal(new[] { "Old", "Mid", "New" }, loop.Select(e => e.Title).ToArray());
    }

    [Fact]
    public void Interleave_Same_UsesStableOrderEveryCycle()
    {
        // Three series, weight=1 each, 4 blocks each (blockSize=1). maxCycles = 4. Under Same interleave the
        // group order is stable per-channel across cycles: the sequence of top-level ids in the loop should be
        // (A B C) repeated 4 times where A/B/C are some stable permutation.
        var a = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var b = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var c = new Guid("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var items = new List<ProgramEntry>();
        items.AddRange(Enumerable.Range(1, 4).Select(i => Ep(a, "Alpha", 1, i, "a" + i, blockSize: 1)));
        items.AddRange(Enumerable.Range(1, 4).Select(i => Ep(b, "Bravo", 1, i, "b" + i, blockSize: 1)));
        items.AddRange(Enumerable.Range(1, 4).Select(i => Ep(c, "Charlie", 1, i, "c" + i, blockSize: 1)));

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle, interleave: InterleaveOrder.Same));

        Assert.Equal(12, loop.Count);
        // The first 3 items reveal the stable order. Cycles 2, 3, 4 must repeat it.
        var cycle1 = loop.Take(3).Select(e => e.TopLevelItemId).ToArray();
        for (var c1 = 1; c1 < 4; c1++)
        {
            var cyc = loop.Skip(c1 * 3).Take(3).Select(e => e.TopLevelItemId).ToArray();
            Assert.Equal(cycle1, cyc);
        }
    }

    [Fact]
    public void Interleave_Shuffled_VariesOrderAcrossCycles()
    {
        // Same setup as above, but InterleaveOrder.Shuffled reshuffles the group order per cycle. At least one
        // cycle should differ from cycle 1 (probability of 4 identical shuffles by chance is ~1/1296).
        var a = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var b = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var c = new Guid("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var items = new List<ProgramEntry>();
        items.AddRange(Enumerable.Range(1, 4).Select(i => Ep(a, "Alpha", 1, i, "a" + i, blockSize: 1)));
        items.AddRange(Enumerable.Range(1, 4).Select(i => Ep(b, "Bravo", 1, i, "b" + i, blockSize: 1)));
        items.AddRange(Enumerable.Range(1, 4).Select(i => Ep(c, "Charlie", 1, i, "c" + i, blockSize: 1)));

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle, interleave: InterleaveOrder.Shuffled));

        Assert.Equal(12, loop.Count);
        var cycles = Enumerable.Range(0, 4)
            .Select(i => loop.Skip(i * 3).Take(3).Select(e => e.TopLevelItemId).ToArray())
            .ToList();
        var distinct = cycles.Distinct(new ArrayComparer<Guid>()).Count();
        Assert.True(distinct > 1, "interleave=Shuffled should produce more than one distinct cycle order across 4 cycles");
    }

    [Fact]
    public void Interleave_Shuffled_IsDeterministic()
    {
        // Same input twice under Shuffled interleave: same output. The per-cycle shuffle is seeded by channel id
        // + cycle number so the guide projection and the live stream compute the same order.
        var a = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var b = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var items = new List<ProgramEntry>();
        items.AddRange(Enumerable.Range(1, 4).Select(i => Ep(a, "Alpha", 1, i, "a" + i, blockSize: 1)));
        items.AddRange(Enumerable.Range(1, 4).Select(i => Ep(b, "Bravo", 1, i, "b" + i, blockSize: 1)));

        var loop1 = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle, interleave: InterleaveOrder.Shuffled));
        var loop2 = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle, interleave: InterleaveOrder.Shuffled));

        Assert.Equal(loop1.Select(e => e.ItemId), loop2.Select(e => e.ItemId));
    }

    [Fact]
    public void Loop_ExhaustsShortestByWrapping()
    {
        // Two groups with mismatched sizes: A has 8 blocks (blockSize=1), B has 3 blocks. Weight=1 both.
        // maxCycles = max(8, 3) = 8. A airs each block once (8 slots). B wraps to fill 8 slots (each of its 3
        // blocks airs 8/3 = 2 or 3 times, deterministically).
        var a = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var b = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var items = new List<ProgramEntry>();
        items.AddRange(Enumerable.Range(1, 8).Select(i => Ep(a, "Alpha", 1, i, "a" + i, blockSize: 1)));
        items.AddRange(Enumerable.Range(1, 3).Select(i => Ep(b, "Bravo", 1, i, "b" + i, blockSize: 1)));

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        Assert.Equal(16, loop.Count);
        Assert.Equal(8, loop.Count(e => e.SeriesId == a));   // all 8 A blocks aired once each
        Assert.Equal(8, loop.Count(e => e.SeriesId == b));   // B slots = 8 (wraps through 3 blocks)
    }

    // Helper for comparing arrays as sequence-equal values inside a HashSet/Distinct.
    private sealed class ArrayComparer<T> : IEqualityComparer<T[]>
    {
        public bool Equals(T[]? x, T[]? y)
            => x is null ? y is null : y is not null && x.SequenceEqual(y);
        public int GetHashCode(T[] obj)
        {
            unchecked
            {
                var h = 17;
                foreach (var v in obj) h = h * 31 + (v?.GetHashCode() ?? 0);
                return h;
            }
        }
    }

    [Fact]
    public void Chronological_UndatedSortsLast()
    {
        var undatedId = Guid.NewGuid();
        var datedId = Guid.NewGuid();
        var items = new[]
        {
            new ProgramEntry(undatedId, "Undated", null, Hour, "/m.mkv") { IsMovie = true, TopLevelItemId = undatedId },
            new ProgramEntry(datedId, "Dated", null, Hour, "/m.mkv") { IsMovie = true, PremiereDate = new DateTime(2000, 1, 1), TopLevelItemId = datedId }
        };

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Chronological));
        Assert.Equal(new[] { "Dated", "Undated" }, loop.Select(e => e.Title).ToArray());
    }
}
