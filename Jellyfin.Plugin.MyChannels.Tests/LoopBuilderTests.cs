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
    // ordering intent (mode, keep-multi-part, per-episode shuffle, channel id for seed, rotation counter).
    private static ChannelLoopOptions Opts(bool keepMulti = true, LoopMode mode = LoopMode.Alphabetical, bool shuffleEp = false, string ch = "ch1", int rotation = 0)
        => new ChannelLoopOptions(keepMulti, mode, shuffleEp, ch, rotation);

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
        var s = Guid.NewGuid();
        var loop = ProgramLoopBuilder.Build(new[]
        {
            Ep(s, "Show", 1, 1, "The Saga (1)", blockSize: 2),
            Ep(s, "Show", 1, 2, "The Saga (2)", blockSize: 2),
            Ep(s, "Show", 1, 3, "The Saga (3)", blockSize: 2)
        }, Opts(mode: LoopMode.Shuffle));

        // With Weight=1 (default), only one block of the series is used per round → loop is capped at
        // blockSize+1 episodes (a pair extending the block by one).
        Assert.True(loop.Count <= 2, "a block glued more than a pair together: " + loop.Count);
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
    public void Shuffle_DefaultWeights_EachGroupContributesOneBlock()
    {
        // Under default weight=1, each series contributes exactly ONE 4-episode block per loop -- so a giant series
        // gets the same footing as smaller ones, and nothing plays back to back.
        var items = new List<ProgramEntry>();
        var big = new Guid("11111111-1111-1111-1111-111111111111");
        items.AddRange(Enumerable.Range(1, 40).Select(i => Ep(big, "Futurama", 1, i, "e" + i, blockSize: 4)));
        for (var s = 0; s < 4; s++)
        {
            var id = new Guid("2222222" + s + "-2222-2222-2222-222222222222");
            items.AddRange(Enumerable.Range(1, 20).Select(i => Ep(id, "Show" + s, 1, i, "e" + i, blockSize: 4)));
        }

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        Assert.Equal(5 * 4, loop.Count);
        Assert.Equal(4, loop.Count(e => e.SeriesId == big));
        Assert.True(MaxRun(loop) <= 4, "a series ran longer than one block: " + MaxRun(loop));
    }

    [Fact]
    public void PerEntryWeight_MultipliesSlotsInRoundRobin()
    {
        // Three series with weights 3, 2, 1 → over a full round the loop contains 3+2+1 = 6 blocks; the weight-3
        // series contributes 3 blocks, weight-2 contributes 2, weight-1 contributes 1.
        var a = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var b = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var c = new Guid("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var items = new List<ProgramEntry>();
        items.AddRange(Enumerable.Range(1, 10).Select(i => Ep(a, "Alpha", 1, i, "a" + i, weight: 3, blockSize: 1)));
        items.AddRange(Enumerable.Range(1, 10).Select(i => Ep(b, "Bravo", 1, i, "b" + i, weight: 2, blockSize: 1)));
        items.AddRange(Enumerable.Range(1, 10).Select(i => Ep(c, "Charlie", 1, i, "c" + i, weight: 1, blockSize: 1)));

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        Assert.Equal(3, loop.Count(e => e.SeriesId == a));
        Assert.Equal(2, loop.Count(e => e.SeriesId == b));
        Assert.Equal(1, loop.Count(e => e.SeriesId == c));
    }

    [Fact]
    public void PerEntryBlockSize_DifferentiatesBlockSizes()
    {
        // Series A with blockSize=3 packs 3 episodes per block; series B with blockSize=1 packs one. With default
        // weight=1 both contribute one block per round, so a full loop is 3 + 1 = 4 items.
        var a = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var b = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var items = new List<ProgramEntry>();
        items.AddRange(Enumerable.Range(1, 6).Select(i => Ep(a, "Alpha", 1, i, "a" + i, blockSize: 3)));
        items.AddRange(Enumerable.Range(1, 6).Select(i => Ep(b, "Bravo", 1, i, "b" + i, blockSize: 1)));

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        Assert.Equal(3, loop.Count(e => e.SeriesId == a));
        Assert.Equal(1, loop.Count(e => e.SeriesId == b));
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
        // Two episodes of one series share TopLevelItemId (the series id), so they inherit the same weight and
        // block size as a single group. Setting weight=4 on both episodes means the group's weight is 4.
        var s = Guid.NewGuid();
        var items = new[]
        {
            Ep(s, "Show", 1, 1, "one", weight: 4, blockSize: 1),
            Ep(s, "Show", 1, 2, "two", weight: 4, blockSize: 1),
            Movie("Solo") // weight=1
        };

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        // Series has 2 blocks available, weight=4 → window wraps: 4 slots that cycle through the 2 blocks. Plus 1
        // movie slot. Total = 5 slots.
        Assert.Equal(5, loop.Count);
        Assert.Equal(4, loop.Count(e => e.SeriesId == s));
    }

    [Fact]
    public void PerEntryWeight_MoviesFavored_OutnumberSeries()
    {
        // Four movies at weight=6 against one series at weight=1: movies dominate the loop.
        var items = new List<ProgramEntry>();
        for (var i = 0; i < 4; i++)
        {
            items.Add(Movie("Movie" + i, weight: 6));
        }

        var show = new Guid("33333333-3333-3333-3333-333333333333");
        items.AddRange(Enumerable.Range(1, 40).Select(i => Ep(show, "Show", 1, i, "e" + i, weight: 1, blockSize: 1)));

        var loop = ProgramLoopBuilder.Build(items, Opts(mode: LoopMode.Shuffle));

        var movieSlots = loop.Count(e => e.SeriesId is null);
        var showSlots = loop.Count(e => e.SeriesId == show);
        Assert.Equal(4 * 6, movieSlots);          // 4 movies × weight 6 = 24 slots
        Assert.Equal(1, showSlots);               // series weight 1 = one block of 1 episode
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
