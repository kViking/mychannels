using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MyChannels.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MyChannels.Api;

/// <summary>
/// Upload/delete endpoint for a channel's custom bumper video. Kept off the base config-save round-trip so
/// a multi-megabyte MP4 never goes anywhere near the plugin config XML: the file is written straight to a
/// cache directory and only a small flag + probed duration are persisted to the channel object.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("livechannels/channels")]
public class BumperController : ControllerBase
{
    // 100 MiB ceiling. A typical 10-30s 1080p60 H.264 bumper is well under 10 MiB; the ceiling is a
    // defence-in-depth guard, not the expected size.
    private const long MaxBumperBytes = 100L * 1024 * 1024;

    private readonly BumperService _bumpers;
    private readonly IApplicationPaths _paths;

    /// <summary>Initializes a new instance of the <see cref="BumperController"/> class.</summary>
    /// <param name="bumpers">Bumper storage service.</param>
    /// <param name="paths">Application paths, used to clear the schedule cache after a change.</param>
    public BumperController(BumperService bumpers, IApplicationPaths paths)
    {
        _bumpers = bumpers;
        _paths = paths;
    }

    /// <summary>
    /// Reports whether a channel has an uploaded bumper and its probed duration, so the editor can render
    /// the current status without re-fetching the whole plugin config.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <returns>An object with <c>hasFile</c> and <c>durationSeconds</c>.</returns>
    [HttpGet("{channelId}/bumper")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetStatus(string channelId)
    {
        var trusted = ResolveTrustedChannelId(channelId);
        if (trusted is null)
        {
            return NotFound();
        }

        var ticks = Plugin.Instance!.ReadConfiguration(c => FindChannel(c, trusted)?.CustomBumperDurationTicks ?? 0);
        return new JsonResult(new
        {
            hasFile = _bumpers.Exists(trusted),
            durationSeconds = ticks > 0 ? ticks / (double)TimeSpan.TicksPerSecond : 0d
        });
    }

    /// <summary>
    /// Uploads a new bumper for the channel. Body is the raw file bytes (Content-Type ignored; the file's
    /// leading bytes are sniffed to confirm MP4). Overwrites any existing bumper. Probes the duration once
    /// and stashes it on the channel so the scheduler doesn't have to re-probe.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The saved bumper's probed duration in seconds.</returns>
    [HttpPost("{channelId}/bumper")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [RequestSizeLimit(MaxBumperBytes)]
    public async Task<ActionResult> Upload(string channelId, CancellationToken cancellationToken)
    {
        var trusted = ResolveTrustedChannelId(channelId);
        if (trusted is null)
        {
            return NotFound("No channel with that id.");
        }

        if (Request.ContentLength is long len && len > MaxBumperBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, "Bumper is too large.");
        }

        var savedPath = await _bumpers.SaveAsync(trusted, Request.Body, cancellationToken).ConfigureAwait(false);
        if (savedPath is null)
        {
            return BadRequest("Bumper must be an MP4 (H.264 + AAC).");
        }

        var ticks = _bumpers.ProbeDurationTicks(savedPath);
        if (ticks <= 0)
        {
            _bumpers.Delete(trusted);
            return BadRequest("ffprobe could not read the bumper's duration; the file may be corrupt.");
        }

        Plugin.Instance!.MutateConfiguration(config =>
        {
            var channel = FindChannel(config, trusted);
            if (channel is null)
            {
                return false;
            }

            channel.HasCustomBumper = true;
            channel.CustomBumperDurationTicks = ticks;
            return true;
        });

        // Clear the schedule cache so the new bumper is picked up on the next tune-in without waiting for a
        // guide refresh. Mirrors the ordering in Plugin.UpdateConfiguration: persist first, then clear, so a
        // concurrent tune-in in the same window doesn't rebuild against the pre-upload channel state.
        ChannelService.ClearScheduleCache(_paths);

        return new JsonResult(new { durationSeconds = ticks / (double)TimeSpan.TicksPerSecond });
    }

    /// <summary>
    /// Deletes the channel's uploaded bumper (file + flag). Silent no-op when nothing was uploaded.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("{channelId}/bumper")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Delete(string channelId)
    {
        var trusted = ResolveTrustedChannelId(channelId);
        if (trusted is null)
        {
            return NotFound("No channel with that id.");
        }

        _bumpers.Delete(trusted);
        Plugin.Instance!.MutateConfiguration(config =>
        {
            var channel = FindChannel(config, trusted);
            if (channel is null || (!channel.HasCustomBumper && channel.CustomBumperDurationTicks == 0))
            {
                return false;
            }

            channel.HasCustomBumper = false;
            channel.CustomBumperDurationTicks = 0;
            return true;
        });

        ChannelService.ClearScheduleCache(_paths);
        return NoContent();
    }

    // Look the incoming id up in the persisted configuration and return the stored id string. That's the
    // channel id every downstream disk-touching call receives, so no path segment reaching the filesystem
    // was ever tainted by the request path. Returns null when the plugin isn't initialised or no channel
    // matches, which the caller translates to a 404. Case-sensitive to match the rest of the plugin's id
    // comparison semantics.
    private static string? ResolveTrustedChannelId(string? requested)
    {
        if (string.IsNullOrEmpty(requested) || Plugin.Instance is null)
        {
            return null;
        }

        return Plugin.Instance.ReadConfiguration(c => FindChannel(c, requested)?.Id);
    }

    private static Models.Channel? FindChannel(Configuration.PluginConfiguration config, string id)
        => config.Channels.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
}
