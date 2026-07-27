# LiveChannels fork — design notes

Local fork of JPKribs/jellyfin-plugin-livechannels. Design deltas from upstream
are tracked here so patches don't get lost across upstream bumps.

## Scope

Two big features, two smaller fixes:

- **Scheduling rewrite**: per-entry weight + block size, layered defaults
  (library → tag/collection → entry), weighted round-robin algorithm ported
  from ErsatzTV. Removes upstream's `FavorKind`/`FavorStrength`.
- **Filler system**: per-channel bumper/trailer pools with chain-fallback
  and auto-generated "Up Next" card for unfillable gaps.
- **Save flow UX**: fewer round-trips (kept), cached RefreshGuide task id
  (kept). Visible-progress attempts (button spinner, window.Loading
  overlay, custom top-right toast) all produced no visible feedback in
  the plugin-page context across v1.0.0.3-1.0.0.5 — the DOM
  manipulations run without error but never paint. Rolled back to the
  green-text-at-end pattern from upstream in v1.0.0.6, kept only the
  round-trip cuts. Worth revisiting only with runtime inspection of
  what's actually happening to DOM changes inside a Jellyfin plugin
  config page container.
- **Library-event cache invalidation**: hook `ItemAdded`/`Updated`/`Removed`
  so new content appears without waiting for the next guide refresh.

Followup work (out of scope for the current release):

- **Scheduled task: pregen Up Next cards** — a background task (analogous to Jellyfin's trickplay image generation) that walks every enabled channel's resolved top-level items and preheats a card per item. Scope is bounded to items actually referenced by a channel — never the whole library. Runs at Idle priority so it's ceded to viewers. Point: full card cache without waiting on the 3-per-resolve budget.
- **Max-cycles cap per channel** — the current full-loop-exhaustion model gives each group `maxCycles × weight` slots per loop, where `maxCycles = max(ceil(blocks/weight))` across groups. That means a weight=1 movie in a channel with a big weight-3 series plays `maxCycles = 7` times per loop, which can feel like a lot. A per-channel `MaxCyclesPerLoop` (clamp) would let users say "no more than N cycles per loop" — smaller cycle counts shorten loops and let low-weight items stay rare relative to high-weight ones. Nice complement to Weight and BlockSize.
- **Content Weights: item list not clearing on channel switch** — intermittent UI bug: switching to a different channel sometimes leaves the previous channel's item rows in the Content Weights section instead of replacing them. Likely a race between `hydrateEntryOverrides` fetches or a missing clear before render. Reproduce, then fix by clearing `#entryOverridesList` at the top of `loadEditor` (before the async hydrate returns) and guarding against out-of-order responses.
- **Rotation shifts by only 1 block per day** — each group's starting block advances by `+1` per calendar day (`start = (rotation + hashOffset) % blocks`). Result: same-time tune-ins on consecutive days land on nearly the same content, and it takes N days to cycle through an N-block series. Users report the "always catching the same episodes" feeling. Fix: hash the rotation INTO the start (`start = ShuffleKey(channelId, groupKey + ":" + rotation) % blocks`) so each day picks an uncorrelated position — random-walk-style variety. Also lets weight-1 single-block items (movies) contribute to variety via cycle-order shuffling. Deterministic still (guide and stream agree), just uncorrelated across days.
- **Config edits don't restart the active session** — saving a channel clears the schedule cache and any new tune-in gets the fresh schedule, but the currently-running ffmpeg producer keeps playing the pre-save program list. Editing a running channel means viewers see the old schedule until they kill the session (Sessions tab) or the session reaps naturally. Fix option A: on `UpdateConfiguration`, kill sessions for channels whose relevant config changed (interrupts viewers briefly but they auto-reconnect). Fix option B: signal the running producer to re-resolve on next item boundary (cleaner, more work). B is nicer.

Future refactor (not yet scoped):

- **Split `livechannels_channels.js`** — currently ~1300 lines in a single
  IIFE-style module with heavy shared closure state (config, channels, ratings,
  cultures, pickers). Splitting into focused modules (io/persist, editor,
  sources, filters, ratings) would help but requires either the shared JS
  package (JPKribs.Jellyfin.Base) to expose more injection points, or an
  in-plugin barrel-style shared helper. Not a first-release change.

Rejected (considered, not doing):

- **Daypart weighting** — themed-channel model makes it useless; would require
  granular per-entry tagging that isn't worth the burden.
- **Tune-in perf tuning** (segment-size knob) — 4.5s warm / ~10s cold is HLS
  reality; only meaningful fix (LL-HLS) is disproportionate work.
- **Rating blocks** (existing upstream feature) — MPAA taxonomy doesn't match
  user's moral sensibilities; left as-is rather than replaced or removed.
- **Guide-vs-stream drift fix** — verified as a reading issue, not real drift.

## Reference projects

- **ErsatzTV** (https://github.com/ErsatzTV/ErsatzTV, C#, zlib licence) — port
  scheduling algorithms and concepts from here. Zlib is MIT-equivalent; attribute.
- **RCS Selector** (proprietary radio traffic system) — inspiration for
  category-based weighted rotation at library scale.

## Terminology

- **Lineup** — the channel's content plan (which series/movies/etc. are in it)
- **Schedule** — the timed projection of the lineup onto wall-clock time
- **Guide** — what the viewer sees (Jellyfin's Live TV EPG)

## Scheduling model (in progress)

Replace upstream's global `EpisodesPerBlock` + `FavorKind`/`FavorStrength` with:

- **Per-entry weight** (int, default 1) — how many slots per round the entry gets
- **Per-entry block size** (int, default 1) — episodes per slot for series
- **Layered defaults**: library-level → tag/collection bucket → per-entry override
  (per-entry-only unusable at library scale of thousands)

`FavorKind`/`FavorStrength` deleted — subsumed by per-entry weight.

Algorithm: weighted round-robin (port from ErsatzTV). Optional selection of
alternative algorithms (Flood, Chronological, Random) also from ErsatzTV.

Weight 0 means "in the lineup but benched" — do NOT overload as filler.

## Filler (in progress)

Per-channel toggle unfolds a form for defining filler sources. Two named pools
(no per-entry weight; pools are small and categorically similar):

- **Bumpers** — short station-ident-style content (5-15s typical)
- **Trailers** — longer content (30s-3min); interstitial-length material lives here too

Each pool takes the same library/collection/entry picker used for main content.
Empty pool = that tier is unused; defining only one is fine.

**Chain-fallback**: prefer trailers for medium gaps, chain bumpers for the
residual. Gap smaller than the shortest available filler → fall back to auto-card
(see below).

**Format support**: whatever ffmpeg can decode (webp, gif, apng, jpg, png, mp4,
webm, mkv, mov, etc). Distinguish still/short-loop images (need `-loop 1` or
`-stream_loop -1` to hold for the fill duration) from proper video files with
inherent duration. Note: source resolution doesn't determine encode cost — a
4K webp still transcodes to whatever the channel's output resolution is.

**Length bounds**: max and min length per pool.

**Unfillable gap handling**: automatic based on what fits.
- Sub-1s: freeze last frame (imperceptible timing slop)
- Gap < shortest available filler: auto-card
- Gap >= shortest available filler: filler chain (trailers preferred, chain
  bumpers for residual)

No manual threshold — the shortest filler in the defined pools is the natural
boundary. Both pools empty → always auto-card. Pad-previous (freeze frame past
~1s) rejected — reads as frozen stream rather than clean handoff.

**Boundary snap** (optional, per-channel): snap program starts to :00 / :15 /
:30 / :60. Off = programs are back-to-back (or gap-filled if filler defined).
On = scheduler pads program-end → next-boundary with filler; empty pool +
snap on = auto-card fills the gap.

## Auto-generated "Up Next" card

For fallback filler and unfillable gaps. Renders server-side via ffmpeg filter
chain, same pattern as `DefaultLogoService`. Cached per-program in memory.

**Background** (fall-through priority):
1. Program's own backdrop (series backdrop for episodes, movie backdrop for movies)
2. Collection backdrop
3. Channel logo blurred + zoomed
4. Solid color from channel palette

**Composition**: dark gradient overlay in the top-right quadrant + white text
in that quadrant. No palette extraction from the backdrop — the gradient
provides contrast on any image, which is why streaming services all use the
same pattern.

**Text**: "Up next: <Program Title>" + when ("at 8:15" or "in 30 seconds").
Optionally episode name for shows.

## Save flow — perf and feedback (in progress)

Current save (`saveChannel` → `persist`) does 4 serial round-trips: getConfig,
saveConfig, getScheduledTasks, startScheduledTask(RefreshGuide). Then a
fire-and-forget refresh whose errors are swallowed. No button state change,
no spinner — status is a text string that's set to "Refreshing…" *before*
the refresh actually happens.

Server-side `UpdateConfiguration` also does eager `ClearScheduleCache` (disk
work) + full XML re-serialize of the whole channels blob.

Fixes to apply (client-first, no rebuild needed for 1-5):

1. Disable button + spinner on click, re-enable on completion
2. Kill the pre-save `getConfig()` — use in-memory `config` directly
3. Cache RefreshGuide task ID at page load, don't re-enumerate every save
4. Await the RefreshGuide start call, surface real completion status
5. Handle refresh errors — surface, don't swallow
6. (C#) Make ClearScheduleCache lazy — mark-invalid, rebuild on next read

## Library-event cache invalidation (in progress)

Current rebuild triggers are only: `RefreshGuide` scheduled task (hourly by
default), config save, and cold tune-in with no cache. Library changes — new
episodes added, items updated, items removed — DO NOT invalidate the cache.
New content takes up to an hour to appear on a channel.

Fix: hook Jellyfin's `ILibraryManager.ItemAdded`, `ItemUpdated`,
`ItemRemoved` events. On any fire, invalidate ALL channel schedule caches
(same effect as `ClearScheduleCache`, minus the config write). Naive
whole-invalidate is fine — cost is one rebuild per channel on next tune-in
or guide refresh, bounded for a home server. Targeted invalidation
(only channels referencing the affected sources) is over-engineering for MVP.
