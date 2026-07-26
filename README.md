# MyChannels

## Credit

- **[JPKribs/jellyfin-plugin-livechannels](https://github.com/JPKribs/jellyfin-plugin-livechannels)** — the entire pre-fork codebase, the plugin architecture, the Jellyfin Live TV wiring, and every feature that already works. All the actual engineering here is his.
- **[ErsatzTV](https://github.com/ErsatzTV/ErsatzTV)** (zlib) — scheduling algorithms and design concepts referenced in this fork's roadmap.

## Honest disclaimer

This is a **vibe-coded personal fork**. "Personal" as in *mine* — this exists because I want features JPKribs' plugin doesn't have yet and my solution was to vibe code it. It's garbage, y'all- not real IP. 

- **Not seeking users.** If you want the reliable, maintained thing, use [the upstream plugin](https://github.com/JPKribs/jellyfin-plugin-livechannels). This is here for me and anyone who wants to poke at the roadmap ideas.
- **AI-assisted (Claude Code) throughout.** Code review is on me; bugs are on me; I basically am doing none

Roadmap and design intent: [`DESIGN.md`](DESIGN.md).

---

## What it is

Fork of [JPKribs/jellyfin-plugin-livechannels](https://github.com/JPKribs/jellyfin-plugin-livechannels) with an expanded scheduling model, filler pools, an auto-generated Up Next card, and event-driven cache invalidation. Channels appear natively in Jellyfin's Live TV.

## Install

Add this repository to Jellyfin's plugin repositories:

```
https://raw.githubusercontent.com/kViking/mychannels/master/manifest.json
```

Then install **MyChannels** from Dashboard → Plugins → Catalog → Live TV, and restart Jellyfin.

## Migrating from JPKribs Live Channels

MyChannels uses a distinct plugin id, so it can be installed alongside the official plugin during transition. Channel configuration migrates via the plugin's built-in JSON export/import.

1. In the official Live Channels config, click **Export** and save the JSON.
2. Add MyChannels' repository URL (above) to Jellyfin's plugin repositories.
3. Uninstall the official Live Channels plugin.
4. Install MyChannels from the catalog and restart Jellyfin.
5. In the new MyChannels config, click **Import** and select the saved JSON.

The export format is preserved from upstream, so the same JSON works as a rollback path back to the official plugin if needed.

## Build

```
nix shell nixpkgs#dotnet-sdk_9 --command bash ./build.sh Release
```

Output: `dist/mychannels-<version>.zip` and its `.md5` checksum.

## License

GPL v3 — inherited from upstream. Preserves the original copyright notice.
