using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MyChannels.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MyChannels.Services;

// ChannelService: resolving a channel's configured sources into the ordered, probed program loop.
public partial class ChannelService
{
    // The item kinds a channel includes, from its Content Types toggles. Episodes are queried when either regular
    // episodes or specials are wanted; the season-0 split between them is applied per item during the build.
    private static BaseItemKind[] BuildKinds(Channel channel)
    {
        var kinds = new List<BaseItemKind>(4);
        if (channel.IncludeMovies)
        {
            kinds.Add(BaseItemKind.Movie);
        }

        if (channel.IncludeEpisodes || channel.IncludeSpecials)
        {
            kinds.Add(BaseItemKind.Episode);
        }

        if (channel.IncludeMusicVideos)
        {
            kinds.Add(BaseItemKind.MusicVideo);
        }

        if (channel.IncludeHomeVideos)
        {
            kinds.Add(BaseItemKind.Video);
        }

        return kinds.ToArray();
    }

    // Reads an item's media streams once, returning an empty list (never throwing) when they cannot be read, so
    // the guide-refresh build can probe every item's metadata with a single query per item and tolerate gaps.
    private IReadOnlyList<MediaStream> SafeGetMediaStreams(Guid itemId)
    {
        try
        {
            return _mediaSourceManager.GetMediaStreams(itemId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read media streams for {ItemId}", itemId);
            return Array.Empty<MediaStream>();
        }
    }

    // The default audio track (the one Jellyfin would play) and its ordinal among the item's audio streams, or
    // null when the item has no audio.
    private static (int Ordinal, MediaStream Stream)? DefaultAudio(IReadOnlyList<MediaStream> streams)
    {
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderBy(s => s.Index).ToList();
        if (audio.Count == 0)
        {
            return null;
        }

        var defaultIndex = audio.FindIndex(s => s.IsDefault);
        var ordinal = defaultIndex >= 0 ? defaultIndex : 0;
        return (ordinal, audio[ordinal]);
    }

    // The item's subtitle streams (ordered by absolute index) as the minimal burn-in descriptors cached on the
    // schedule, so the live stream picks a burn-in track without re-reading the media streams.
    private static SubtitleStreamInfo[] BuildSubtitleInfos(IReadOnlyList<MediaStream> streams)
    {
        var subtitles = streams.Where(s => s.Type == MediaStreamType.Subtitle).OrderBy(s => s.Index).ToList();
        if (subtitles.Count == 0)
        {
            return Array.Empty<SubtitleStreamInfo>();
        }

        var infos = new SubtitleStreamInfo[subtitles.Count];
        for (var i = 0; i < subtitles.Count; i++)
        {
            var s = subtitles[i];
            infos[i] = new SubtitleStreamInfo
            {
                RelativeIndex = i,
                AbsoluteIndex = s.Index,
                IsForced = s.IsForced,
                IsDefault = s.IsDefault,
                IsText = s.IsTextSubtitleStream
            };
        }

        return infos;
    }

    // Whether the default audio track is in the channel's required language (a three-letter ISO code). An empty
    // language allows everything. Strict by design ("MUST be this language"): an item whose default track is
    // another language, is untagged, or cannot be read is excluded. Operates on the already-read streams.
    private static bool AudioLanguageAllows(string language, IReadOnlyList<MediaStream> streams)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return true;
        }

        var def = DefaultAudio(streams);
        return def is not null && string.Equals(def.Value.Stream.Language, language, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enumerates the "top-level" items resolved from a channel's sources, WITHOUT expanding series to their
    /// episodes. For a Library source with a Genre or AllContent selection, episodes returned by the library
    /// query are rolled up to their parent series (so a series appears once, not as N episodes). For Whitelist
    /// or Collection sources, the source's directly-referenced items are returned as-is (Series stay as Series).
    /// Deduplicated across sources by id.
    /// </summary>
    /// <remarks>
    /// Read-only: safe to run concurrently with a guide refresh. Used by the Content Weights editor and the
    /// legacy-FavorKind deprecation logger. Applies the same rating and kind filters as <see cref="BuildPrograms"/>,
    /// but skips audio-language filtering (which needs a representative episode's media streams and is not worth
    /// the cost at series level).
    /// </remarks>
    /// <param name="channel">The channel whose sources to enumerate.</param>
    /// <returns>The channel's top-level items, in source-iteration order, deduplicated.</returns>
    public IEnumerable<BaseItem> EnumerateTopLevelItems(Channel channel)
    {
        if (channel is null || channel.Sources is null)
        {
            yield break;
        }

        var ratingBlocks = ResolveRatingBlocks(channel);
        var ratings = HasTimeOfDayRating(ratingBlocks)
            ? new RatingFilter(null, null, true)
            : EffectiveSingleBandFilter(ratingBlocks);
        var kinds = BuildKinds(channel);
        if (kinds.Length == 0)
        {
            yield break;
        }

        var seen = new HashSet<Guid>();
        foreach (var source in channel.Sources)
        {
            IEnumerable<BaseItem> raw;
            if (source.Kind == SourceKind.Collection)
            {
                if (!Guid.TryParse(source.CollectionId, out var boxId)
                    || _libraryManager.GetItemById(boxId) is not BoxSet box)
                {
                    continue;
                }

                raw = box.GetLinkedChildren();
            }
            else
            {
                if (string.IsNullOrEmpty(source.LibraryId) || !Guid.TryParse(source.LibraryId, out var libraryId))
                {
                    continue;
                }

                if (source.Selection == SelectionMode.Whitelist)
                {
                    raw = source.ItemIds
                        .Select(id => _libraryManager.GetItemById(id))
                        .Where(i => i is not null)!;
                }
                else
                {
                    // Reuse the same resolver BuildPrograms uses so the two stay in sync, then roll episodes up
                    // to their parent series.
                    raw = RollupToTopLevel(ResolveSource(source, libraryId, ratings, kinds));
                }
            }

            foreach (var item in raw)
            {
                if (item is null || !seen.Add(item.Id))
                {
                    continue;
                }

                yield return item;
            }
        }
    }

    // Rolls a resolved item list up to top-level entities: an Episode collapses to its Series (fetched once),
    // any other item is returned as itself. Deduplicated within this rollup so a series with many resolved
    // episodes appears once.
    private IEnumerable<BaseItem> RollupToTopLevel(IEnumerable<BaseItem> items)
    {
        var seen = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (item is Episode ep && ep.SeriesId != Guid.Empty)
            {
                if (seen.Add(ep.SeriesId))
                {
                    var series = _libraryManager.GetItemById(ep.SeriesId);
                    if (series is not null)
                    {
                        yield return series;
                    }
                }
            }
            else if (seen.Add(item.Id))
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// Resolves a channel's items into the ordered, schedulable loop it cycles through. Content is the union
    /// of every library source; items without a playable file or a positive runtime are dropped because they
    /// cannot be placed on the timeline.
    /// </summary>
    /// <param name="channel">The channel to resolve.</param>
    /// <returns>The ordered program loop.</returns>
    private IReadOnlyList<ProgramEntry> BuildPrograms(Channel channel)
    {
        if (string.Equals(channel.Id, PopularChannelId, StringComparison.Ordinal))
        {
            return ResolvePopularPrograms(channel);
        }

        // Rating limits: a channel with time-of-day blocks defers rating to the daypart schedule (so the pool holds
        // every rating and the schedule picks per window); otherwise the all-day band is applied here at build time.
        var ratingBlocks = ResolveRatingBlocks(channel);
        var ratings = HasTimeOfDayRating(ratingBlocks)
            ? new RatingFilter(null, null, true)
            : EffectiveSingleBandFilter(ratingBlocks);
        var years = new YearFilter(channel.Years);
        var kinds = BuildKinds(channel);
        if (kinds.Length == 0)
        {
            // No content types are enabled, so nothing can air. Return empty rather than risk an unfiltered query.
            return Array.Empty<ProgramEntry>();
        }

        var byId = new Dictionary<Guid, BaseItem>();
        var libraryIds = new List<Guid>();
        foreach (var source in channel.Sources)
        {
            if (source.Kind == SourceKind.Collection)
            {
                foreach (var item in CollectionItems(source, ratings, kinds))
                {
                    byId[item.Id] = item;
                }

                continue;
            }

            if (string.IsNullOrEmpty(source.LibraryId) || !Guid.TryParse(source.LibraryId, out var libraryId))
            {
                continue;
            }

            libraryIds.Add(libraryId);
            foreach (var item in ResolveSource(source, libraryId, ratings, kinds))
            {
                byId[item.Id] = item;
            }
        }

        // Additional channel-wide filters, resolved once. Studios match the item or its series (so a network on the
        // series carries to its episodes); people are resolved to the set of items they appear in via one query per
        // library; the rating floors and year set are simple per-item checks. Studios and people are library-scoped,
        // but a collection source can pull from any library, so when one is present these resolve across every
        // library (an empty scope) so collection items are covered the same as library items.
        var hasCollection = channel.Sources.Any(s => s.Kind == SourceKind.Collection);
        var filterScope = hasCollection ? new List<Guid>() : libraryIds;
        var studios = BuildStudioSet(channel.Studios);
        var seriesStudios = studios.Count > 0 ? BuildSeriesStudioMap(filterScope) : new Dictionary<Guid, HashSet<string>>();
        var peopleAllowed = channel.People.Any(p => p.Id != Guid.Empty)
            ? ResolvePeopleAllowed(channel.People, filterScope, kinds)
            : null;

        // Fold the sparse EntryOverrides list into a lookup keyed by item id (a series/movie/etc. top-level id).
        // Absent ids default to weight=1, blockSize=1 downstream.
        var overridesByItemId = channel.EntryOverrides
            .Where(o => o.ItemId != Guid.Empty)
            .GroupBy(o => o.ItemId)
            .ToDictionary(g => g.Key, g => g.First());

        var entries = new List<ProgramEntry>();
        foreach (var item in byId.Values)
        {
            if (item is Episode ep)
            {
                var special = ep.ParentIndexNumber == 0;
                if (special ? !channel.IncludeSpecials : !channel.IncludeEpisodes)
                {
                    continue;
                }
            }

            // Cheap per-item gates first, so a filtered item never pays the media read below. Year, rating floors,
            // studios, and (when active) the people set each narrow the channel independently.
            if (!years.Allows(item.ProductionYear)
                || !PassesMinRating(item.CommunityRating, channel.MinCommunityRating)
                || !PassesMinRating(item.CriticRating, channel.MinCriticRating)
                || !PassesStudios(studios, EffectiveStudios(item, seriesStudios))
                || (peopleAllowed is not null && !peopleAllowed.Contains(item.Id)))
            {
                continue;
            }

            // Read the item's media streams once and reuse them for both the audio-language filter and the entry's
            // probed metadata, so the whole channel is probed with a single query per item -- here, off the tune-in
            // path -- instead of repeatedly at playback.
            var streams = SafeGetMediaStreams(item.Id);
            if (!AudioLanguageAllows(channel.AudioLanguage, streams))
            {
                continue;
            }

            var entry = ToEntry(item, streams, overridesByItemId);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        var options = new ChannelLoopOptions(
            channel.KeepMultiPartTogether,
            channel.EffectiveLoopMode(),
            channel.ShuffleEpisodes,
            channel.Id,
            LoopRotation(),
            channel.InterleaveOrder);

        var loop = ProgramLoopBuilder.Build(entries, options);
        return channel.FillerMode == FillerMode.Off ? loop : InjectFillerCards(channel, loop);
    }

    // Interleaves auto Up Next cards into a raw loop, one before every program, based on the channel's
    // FillerMode. For SnapToBoundary the CARD FILE is always generated at the max possible pad length
    // (SnapMinutes) and truncated at stream time to fit the actual pad — that way one card per program
    // covers every possible pad duration, so the card set stays bounded across loop reshuffles and
    // rotation shifts. FixedBumper cards are always the same fixed length, so file-duration and
    // scheduled-duration agree naturally. Cards are inserted only when the cache file exists; the first
    // few misses are preheated (nice priority, one ffmpeg at a time).
    private IReadOnlyList<ProgramEntry> InjectFillerCards(Channel channel, IReadOnlyList<ProgramEntry> loop)
    {
        if (loop.Count == 0)
        {
            return loop;
        }

        const int PreheatBudget = 3;
        var preheatRemaining = PreheatBudget;

        var result = new List<ProgramEntry>(loop.Count * 2);
        long? previousDurationTicks = null;
        for (var i = 0; i < loop.Count; i++)
        {
            var program = loop[i];
            var padTicks = CardTicksFor(channel, previousDurationTicks);
            if (padTicks > 0)
            {
                var truncate = channel.FillerMode == FillerMode.SnapToBoundary;
                var fileTicks = truncate ? Math.Max(1, channel.SnapMinutes) * TimeSpan.TicksPerMinute : padTicks;
                var cardPath = _autoCardService.EnsureCard(channel.Id, program, TimeSpan.FromTicks(fileTicks));
                if (!string.IsNullOrEmpty(cardPath))
                {
                    result.Add(BuildCardEntry(program, padTicks, cardPath, truncate));
                }
                else if (preheatRemaining > 0)
                {
                    _autoCardService.Preheat(channel.Id, program, TimeSpan.FromTicks(fileTicks));
                    preheatRemaining--;
                }
            }

            result.Add(program);
            previousDurationTicks = program.DurationTicks;
        }

        return result;
    }

    // The card duration in ticks for the slot BEFORE the next program. FixedBumper uses a channel-wide
    // constant. SnapToBoundary derives the pad from the previous program's duration: enough time to bring
    // the previous slot's end up to the next SnapMinutes boundary. The first program has no previous, so
    // FixedBumper still gets its card and SnapToBoundary skips its card (no gap to fill).
    private static long CardTicksFor(Channel channel, long? previousDurationTicks)
    {
        if (channel.FillerMode == FillerMode.FixedBumper)
        {
            var seconds = Math.Max(1, channel.BumperSeconds);
            return seconds * TimeSpan.TicksPerSecond;
        }

        if (channel.FillerMode == FillerMode.SnapToBoundary && previousDurationTicks is long prev)
        {
            var snapMinutes = Math.Max(1, channel.SnapMinutes);
            var snapTicks = snapMinutes * TimeSpan.TicksPerMinute;
            var overflow = prev % snapTicks;
            return overflow == 0 ? 0 : snapTicks - overflow;
        }

        return 0;
    }

    // Synthesises a card ProgramEntry: real file path so the streaming pipeline reads it like any other
    // program, its own item id derived from the target program's id + scheduled duration so the schedule
    // cache treats it as a stable entry across refreshes. Title is what appears in the guide. When
    // truncate is true, the stream caps playback at DurationTicks even though the file on disk is longer.
    private static ProgramEntry BuildCardEntry(ProgramEntry nextProgram, long ticks, string cardPath, bool truncate)
    {
        // Deterministic id: same next-program + same scheduled duration always yields the same card id.
        var idBytes = new byte[16];
        var src = nextProgram.ItemId.ToByteArray();
        Array.Copy(src, idBytes, 16);
        idBytes[15] = (byte)(idBytes[15] ^ 0xC0);   // flip a bit so the card id never collides with the program id
        var cardId = new Guid(idBytes);

        var title = "Up Next: " + (nextProgram.SeriesName ?? nextProgram.Title ?? "next program");
        return new ProgramEntry(cardId, title, null, ticks, cardPath)
        {
            IsMovie = false,
            SeriesId = null,
            SeriesName = null,
            RawName = title,
            TopLevelItemId = cardId,   // its own group so the loop builder never grouped it with anything
            Weight = 1,
            BlockSize = 1,
            TruncateToDuration = truncate
        };
    }

    // A rotation counter (days since the Unix epoch) that advances which single block each series contributes to a
    // shuffled loop. It is captured into the cached schedule when the schedule is built, so the guide and the live
    // stream always agree, and it advances day over day so a channel works through each series across refreshes.
    private static int LoopRotation() => (int)(DateTime.UtcNow - DateTime.UnixEpoch).TotalDays;

    // Resolves one library source to its matching items (before specials/ordering are applied). The
    // selection mode picks exactly one narrowing: all content, a genre filter, a whitelist, or a blacklist.
    private IEnumerable<BaseItem> ResolveSource(LibrarySource source, Guid libraryId, RatingFilter ratings, BaseItemKind[] kinds)
        => source.Selection switch
        {
            SelectionMode.Genre => GenreItems(libraryId, source, ratings, kinds),
            SelectionMode.Whitelist => WhitelistItems(source, ratings, kinds),
            SelectionMode.Blacklist => BlacklistItems(libraryId, source, ratings, kinds),
            _ => QueryLibrary(libraryId, Array.Empty<string>(), ratings, kinds)
        };

    // The library narrowed by genre, matched against each item's own genres and, for an episode, its series'
    // genres too (so a series-level tag like "Anime" matches its episodes even when the episodes are untagged).
    // Included genres match any (OR) or every (AND) genre; excluded genres drop any item that carries one. With
    // no genres at all this is the whole library.
    private IEnumerable<BaseItem> GenreItems(Guid libraryId, LibrarySource source, RatingFilter ratings, BaseItemKind[] kinds)
    {
        var include = source.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();
        var exclude = source.ExcludeGenres.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();
        if (include.Length == 0 && exclude.Length == 0)
        {
            return QueryLibrary(libraryId, Array.Empty<string>(), ratings, kinds);
        }

        // Series genres apply to their episodes, so build a seriesId -> genres lookup once and use it to compute
        // each item's effective genres below.
        var seriesGenres = SeriesGenreMap(libraryId);

        // Candidates: the whole library when only excluding, else items whose own genres match (database-filtered)
        // unioned with the episodes of series whose genres match (so a series-level tag is honoured).
        IEnumerable<BaseItem> items;
        if (include.Length == 0)
        {
            items = QueryLibrary(libraryId, Array.Empty<string>(), ratings, kinds);
        }
        else
        {
            var direct = QueryLibrary(libraryId, include, ratings, kinds);
            var viaSeries = EpisodesOfSeries(SeriesMatching(libraryId, include))
                .Where(e => KindAllowed(e, kinds) && ratings.Allows(e));
            items = direct.Concat(viaSeries).DistinctBy(i => i.Id);
        }

        // Refine the OR-union candidates by the requested match mode, then drop anything carrying an excluded
        // genre. Both checks run against effective (own + series) genres.
        return items.Where(i =>
        {
            var eff = EffectiveGenres(i, seriesGenres);
            var includeOk = include.Length == 0
                || (source.MatchAllGenres ? include.All(eff.Contains) : include.Any(eff.Contains));
            var excludeOk = exclude.Length == 0 || !exclude.Any(eff.Contains);
            return includeOk && excludeOk;
        });
    }

    // A seriesId -> genres lookup for every series in the library, so episode genre matching can include the
    // parent series' genres. One indexed query, far smaller than enumerating episodes.
    private Dictionary<Guid, HashSet<string>> SeriesGenreMap(Guid libraryId)
    {
        var map = new Dictionary<Guid, HashSet<string>>();
        var series = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            AncestorIds = new[] { libraryId },
            Recursive = true,
            IsVirtualItem = false
        });

        foreach (var s in series)
        {
            map[s.Id] = new HashSet<string>(s.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        return map;
    }

    // The ids of series in the library carrying any of the genres (database-filtered: one indexed query).
    private List<Guid> SeriesMatching(Guid libraryId, string[] genres)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            AncestorIds = new[] { libraryId },
            Genres = genres,
            Recursive = true,
            IsVirtualItem = false
        }).Select(s => s.Id).ToList();

    // An item's effective genres for matching: its own, plus its series' when it is an episode.
    private static HashSet<string> EffectiveGenres(BaseItem item, Dictionary<Guid, HashSet<string>> seriesGenres)
    {
        var set = new HashSet<string>(item.Genres ?? (IReadOnlyList<string>)Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (item is Episode ep && ep.SeriesId != Guid.Empty && seriesGenres.TryGetValue(ep.SeriesId, out var sg))
        {
            set.UnionWith(sg);
        }

        return set;
    }

    // The explicitly chosen shows and movies (series expand to their episodes), kept to playable kinds
    // within the rating cap.
    private IEnumerable<BaseItem> WhitelistItems(LibrarySource source, RatingFilter ratings, BaseItemKind[] kinds)
    {
        var result = new List<BaseItem>();
        foreach (var id in new HashSet<Guid>(source.ItemIds))
        {
            var item = _libraryManager.GetItemById(id);
            if (item is null)
            {
                continue;
            }

            if (item is Series)
            {
                result.AddRange(EpisodesOf(id));
            }
            else
            {
                result.Add(item);
            }
        }

        return result.Where(i => KindAllowed(i, kinds) && ratings.Allows(i));
    }

    // The members of a collection (box set), expanding a member series to its episodes, kept to playable kinds
    // within the rating cap. Collections can span libraries, so this resolves the collection's linked children
    // rather than issuing a library query.
    private IEnumerable<BaseItem> CollectionItems(LibrarySource source, RatingFilter ratings, BaseItemKind[] kinds)
    {
        if (!Guid.TryParse(source.CollectionId, out var id) || _libraryManager.GetItemById(id) is not BoxSet set)
        {
            return Array.Empty<BaseItem>();
        }

        var result = new List<BaseItem>();
        foreach (var member in set.GetLinkedChildren())
        {
            if (member is Series)
            {
                result.AddRange(EpisodesOf(member.Id));
            }
            else
            {
                result.Add(member);
            }
        }

        return result.Where(i => KindAllowed(i, kinds) && ratings.Allows(i));
    }

    // Everything in the library except the chosen shows/movies and their episodes.
    private IEnumerable<BaseItem> BlacklistItems(Guid libraryId, LibrarySource source, RatingFilter ratings, BaseItemKind[] kinds)
    {
        var chosen = new HashSet<Guid>(source.ItemIds);
        return QueryLibrary(libraryId, Array.Empty<string>(), ratings, kinds).Where(i =>
            !chosen.Contains(i.Id) &&
            !(i is Episode ep && ep.SeriesId != Guid.Empty && chosen.Contains(ep.SeriesId)));
    }

    private static bool KindAllowed(BaseItem item, BaseItemKind[] kinds)
        => Array.IndexOf(kinds, item.GetBaseItemKind()) >= 0;

    private IReadOnlyList<BaseItem> EpisodesOf(Guid seriesId)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { seriesId },
            Recursive = true,
            IsVirtualItem = false
        });

    // The episodes of every matching series in one batched query (AncestorIds matches descendants of any listed
    // series), instead of a GetItemList call PER series. A genre matching hundreds of series otherwise ran hundreds
    // of sequential queries and stalled a large channel's start-up for ~40s -- past the live-playlist deadline, so
    // the stream handed Jellyfin an empty playlist and the client reported "Failed to load". Chunked so a genre
    // matching very many series cannot overflow the query's host-parameter limit.
    private IReadOnlyList<BaseItem> EpisodesOfSeries(List<Guid> seriesIds)
    {
        if (seriesIds.Count == 0)
        {
            return Array.Empty<BaseItem>();
        }

        const int batchSize = 200;
        var episodes = new List<BaseItem>();
        for (var start = 0; start < seriesIds.Count; start += batchSize)
        {
            var ancestors = seriesIds.Skip(start).Take(batchSize).ToArray();
            episodes.AddRange(_libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = ancestors,
                Recursive = true,
                IsVirtualItem = false
            }));
        }

        return episodes;
    }

    private List<BaseItem> QueryLibrary(Guid libraryId, string[] genres, RatingFilter ratings, BaseItemKind[] kinds)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = kinds,
            AncestorIds = new[] { libraryId },
            Genres = genres,
            Recursive = true,
            IsVirtualItem = false
        });

        return items.Where(ratings.Allows).ToList();
    }

    // The set of item ids the channel's people appear in, resolved with one PersonIds query per library (all
    // libraries when none are listed, for the Popular channel). An item passes the people filter when it is in this
    // set. Built only when a people filter is active; an empty people list returns an empty set.
    private HashSet<Guid> ResolvePeopleAllowed(IEnumerable<PersonRef> people, IReadOnlyCollection<Guid> libraryIds, BaseItemKind[] kinds)
    {
        var allowed = new HashSet<Guid>();
        var personIds = people.Where(p => p.Id != Guid.Empty).Select(p => p.Id).Distinct().ToArray();
        if (personIds.Length == 0)
        {
            return allowed;
        }

        void Add(InternalItemsQuery query)
        {
            foreach (var item in _libraryManager.GetItemList(query))
            {
                allowed.Add(item.Id);
            }
        }

        if (libraryIds.Count == 0)
        {
            Add(new InternalItemsQuery { PersonIds = personIds, IncludeItemTypes = kinds, Recursive = true, IsVirtualItem = false });
        }
        else
        {
            foreach (var libraryId in libraryIds)
            {
                Add(new InternalItemsQuery { PersonIds = personIds, AncestorIds = new[] { libraryId }, IncludeItemTypes = kinds, Recursive = true, IsVirtualItem = false });
            }
        }

        return allowed;
    }

    private ProgramEntry? ToEntry(BaseItem? item, IReadOnlyList<MediaStream> streams, IReadOnlyDictionary<Guid, EntryOverride> overridesByItemId)
    {
        if (item is null)
        {
            return null;
        }

        // Prefer an observed real duration over the metadata runtime; drifted metadata otherwise puts a
        // timestamp gap or overlap at this item's seam every loop.
        var metadataTicks = item.RunTimeTicks ?? 0;
        var ticks = ObservedDurationTicks(item.Id, metadataTicks) ?? metadataTicks;
        if (ticks <= 0 || string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        var rawName = string.IsNullOrWhiteSpace(item.Name) ? "Untitled" : item.Name;
        var asEpisode = item as Episode;
        var seriesName = asEpisode?.SeriesName;
        var title = !string.IsNullOrWhiteSpace(seriesName) ? seriesName + " - " + rawName : rawName;
        var seriesId = asEpisode is not null && asEpisode.SeriesId != Guid.Empty ? asEpisode.SeriesId : (Guid?)null;

        // Probe the media metadata the live stream needs to choose its decode pipeline and burn-in track, once,
        // here at refresh, from the streams already read. The stream pipeline reads these off the cached entry and
        // never queries the media streams itself.
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (IsDolbyVisionProfile5(video))
        {
            _logger.LogWarning(
                "Live Channels: excluding \"{Title}\": Dolby Vision Profile 5 has no HDR10-compatible base layer, so every tone mapper renders it with wrong (green/purple) colours. A Profile 8 or HDR10 version of it will play correctly.",
                title);
            return null;
        }

        var defaultAudio = DefaultAudio(streams);

        // Top-level id = the user-visible entity this item belongs to. For an episode that's its series; for a
        // movie/music-video/loose-video, it's the item itself. Every entry that shares a top-level id shares its
        // (Weight, BlockSize) override — that's how a per-series weight applies to all the series' episodes.
        var topLevelId = seriesId ?? item.Id;
        overridesByItemId.TryGetValue(topLevelId, out var ov);
        var weight = ov?.Weight ?? 1;
        var blockSize = ov?.BlockSize ?? 1;

        return new ProgramEntry(item.Id, title, item.Overview, ticks, item.Path)
        {
            Year = item.ProductionYear,
            OfficialRating = item.OfficialRating,
            ParentalRatingValue = item.InheritedParentalRatingValue,
            Genres = item.Genres ?? Array.Empty<string>(),
            SeasonNumber = asEpisode?.ParentIndexNumber,
            EpisodeNumber = asEpisode?.IndexNumber,
            IsMovie = item.GetBaseItemKind() == BaseItemKind.Movie,
            SeriesId = seriesId,
            SeriesName = seriesName,
            RawName = rawName,
            GuideImagePath = ResolveGuideImage(item),
            SourceHeight = item.Height,
            DateAdded = item.DateCreated,
            CommunityRating = item.CommunityRating,
            PremiereDate = item.PremiereDate,
            IsHdr = ComputeIsHdr(video),
            DefaultAudioOrdinal = defaultAudio?.Ordinal ?? 0,
            DefaultAudioLanguage = defaultAudio?.Stream.Language,
            Subtitles = BuildSubtitleInfos(streams),
            TopLevelItemId = topLevelId,
            Weight = Math.Max(1, weight),
            BlockSize = Math.Max(1, blockSize)
        };
    }

    // Picks landscape-friendly guide artwork: a movie's backdrop, otherwise the primary image (episode and
    // music-video primaries are already landscape thumbnails). Falls back to the other type so a program still
    // shows something when its preferred art is missing.
    private static string? ResolveGuideImage(BaseItem item)
    {
        var isMovie = item.GetBaseItemKind() == BaseItemKind.Movie;
        var preferred = isMovie ? ImageType.Backdrop : ImageType.Primary;
        var fallback = isMovie ? ImageType.Primary : ImageType.Backdrop;

        if (item.HasImage(preferred))
        {
            return item.GetImagePath(preferred, 0);
        }

        if (item.HasImage(fallback))
        {
            return item.GetImagePath(fallback, 0);
        }

        return null;
    }
}
