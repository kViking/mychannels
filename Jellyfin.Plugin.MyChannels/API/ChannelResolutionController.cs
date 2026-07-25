using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MyChannels.Models;
using Jellyfin.Plugin.MyChannels.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MyChannels.Api;

/// <summary>
/// Endpoint the channel editor calls to enumerate a channel's "top-level items" (series, movies, collections,
/// etc.) for the Content Weights section — the list of rows the user assigns per-entry Weight and BlockSize
/// overrides to. Accepts the in-memory channel object as POST body so the editor can call it mid-edit without
/// forcing a save first.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("livechannels/channels")]
public class ChannelResolutionController : ControllerBase
{
    private readonly ChannelService _channels;

    /// <summary>Initializes a new instance of the <see cref="ChannelResolutionController"/> class.</summary>
    /// <param name="channels">The channel service, which resolves sources to top-level items.</param>
    public ChannelResolutionController(ChannelService channels)
    {
        _channels = channels;
    }

    /// <summary>
    /// Resolves the channel's sources to their top-level items (series/movies/collections) without expanding
    /// series to episodes. The response is a flat list of one row per unique top-level item; the client uses
    /// each row's id to key its Weight/BlockSize inputs.
    /// </summary>
    /// <param name="channel">The channel to resolve (typically the in-memory channel being edited).</param>
    /// <returns>The top-level items in source order, deduplicated by id.</returns>
    [HttpPost("resolve-top-level")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult ResolveTopLevel([FromBody] Channel channel)
    {
        if (channel is null)
        {
            return BadRequest("A channel body is required.");
        }

        try
        {
            var rows = _channels.EnumerateTopLevelItems(channel)
                .Select(ToRow)
                .ToList();
            return new JsonResult(rows);
        }
        catch (Exception ex)
        {
            return BadRequest("Could not enumerate top-level items: " + ex.Message);
        }
    }

    private static object ToRow(BaseItem item) => new
    {
        id = item.Id,
        name = item.Name ?? string.Empty,
        kind = KindOf(item),
        year = item.ProductionYear,
        childCount = ChildCountOf(item)
    };

    private static string KindOf(BaseItem item) => item switch
    {
        Series => "Series",
        Season => "Season",
        Episode => "Episode",
        Movie => "Movie",
        BoxSet => "Collection",
        _ => item.GetType().Name
    };

    // Approximate child count for series/collections so the UI can hint "12 episodes" or "5 items". Falls back
    // to null when the count isn't cheaply available.
    private static int? ChildCountOf(BaseItem item) => item switch
    {
        Series s => s.GetChildCount(null) is int c and > 0 ? c : (int?)null,
        BoxSet b => b.LinkedChildren?.Length,
        _ => null
    };
}
