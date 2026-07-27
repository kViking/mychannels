using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MyChannels.Configuration;
using Jellyfin.Plugin.MyChannels.Models;
using Jellyfin.Plugin.MyChannels.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MyChannels;

/// <summary>
/// Main plugin entry point for Live Channels.
/// </summary>
public class Plugin : PluginBase<Plugin, PluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    /// <param name="logger">The logger.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _applicationPaths = applicationPaths;

        // Time-of-day schedules chain from the last configuration save. An install upgrading past that feature
        // has no recorded save yet, so stamp one now; without it every build would have to invent an anchor.
        MutateConfiguration(config =>
        {
            if (config.ScheduleAnchorUtc != default)
            {
                return false;
            }

            config.ScheduleAnchorUtc = DateTime.UtcNow;
            return true;
        });

        // v1.1.0.0 deprecates FavorKind/FavorStrength/EpisodesPerBlock in favour of per-entry
        // EntryOverrides. Log a warning per channel that still has a favor setting from the pre-v1.1 model so
        // the user knows their intent isn't being applied and can set per-entry weights instead. Synthesising
        // equivalent overrides automatically would need library queries that aren't safely available during
        // plugin construction, so we surface the deprecation and leave the setting to the user.
        foreach (var channel in ReadConfiguration(c => new List<Channel>(c.Channels)))
        {
            if (channel.FavorKind != FavorKind.None && channel.EntryOverrides.Count == 0)
            {
                logger.LogWarning(
                    "MyChannels: channel '{Name}' still has the deprecated FavorKind={FavorKind}/FavorStrength={Strength} setting. Ignored by the v1.1 scheduler; set per-entry Weight in the Content Weights section instead.",
                    channel.Name,
                    channel.FavorKind,
                    channel.FavorStrength);
            }
        }

        logger.LogInformation("MyChannels plugin initialized");
    }

    private readonly IApplicationPaths _applicationPaths;

    /// <inheritdoc />
    public override string Name => "MyChannels";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("65679cc2-98ed-419b-bc2c-b0110ffc0394");

    /// <inheritdoc />
    public override string Description =>
        "Fork of JPKribs Live Channels with an expanded scheduling model, filler pools, and an auto-generated Up Next card.";

    /// <summary>
    /// Validates incoming configuration before persisting it. The dashboard enforces the same rules in the
    /// browser, but configuration can arrive from any API client.
    /// </summary>
    /// <param name="configuration">The incoming configuration.</param>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration config)
        {
            ConfigurationValidator.Validate(config);

            // Every save re-anchors the time-of-day schedule chain (the cache below is dropped anyway, so the
            // reshuffle this causes coincides with the rebuild the user already expects from saving).
            config.ScheduleAnchorUtc = DateTime.UtcNow;
        }

        // Order matters: persist the new configuration FIRST, then clear the schedule cache. Otherwise a
        // concurrent tune-in or guide refresh firing between the clear and the persist would rebuild the
        // cache using the OLD in-memory config and write it back, so a user turning FillerMode off (say)
        // could see their newly-cleared cache immediately repopulate with cards from the pre-save config.
        base.UpdateConfiguration(configuration);
        ChannelService.ClearScheduleCache(_applicationPaths);
    }

    /// <inheritdoc />
    public override IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = typeof(Plugin).Namespace;

        yield return new PluginPageInfo
        {
            Name = "livechannels_channels",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_channels.html",
            MenuSection = "server",
            DisplayName = "MyChannels",
            EnableInMainMenu = true
        };

        yield return new PluginPageInfo
        {
            Name = "livechannels_channels.js",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_channels.js"
        };

        // Tab 2: Popular channel (the built-in channel 0's own settings).
        yield return new PluginPageInfo
        {
            Name = "livechannels_popular",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_popular.html"
        };

        yield return new PluginPageInfo
        {
            Name = "livechannels_popular.js",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_popular.js"
        };

        // Tab 3: Settings (plugin-wide configuration).
        yield return new PluginPageInfo
        {
            Name = "livechannels_settings",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_settings.html"
        };

        yield return new PluginPageInfo
        {
            Name = "livechannels_settings.js",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_settings.js"
        };

        yield return new PluginPageInfo
        {
            Name = "livechannels_sessions",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_sessions.html"
        };

        yield return new PluginPageInfo
        {
            Name = "livechannels_sessions.js",
            EmbeddedResourcePath = $"{ns}.Configuration.livechannels_sessions.js"
        };

        yield return new PluginPageInfo
        {
            Name = "livechannels_symbols.ttf",
            EmbeddedResourcePath = $"{ns}.Assets.MaterialSymbolsOutlined.ttf"
        };

        // Shared base CSS and JS compiled in from the JPKribs.Jellyfin.Base package.
        foreach (var page in GetSharedPages("livechannels"))
        {
            yield return page;
        }
    }
}
