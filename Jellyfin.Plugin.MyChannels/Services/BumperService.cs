using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MyChannels.Services;

/// <summary>
/// Owns the on-disk custom-bumper files uploaded per channel. One file per channel, stored under
/// <c><cache>/livechannels/bumpers/&lt;channelId&gt;.mp4</c>. Kept out of the plugin config XML: a channel's
/// config carries only the <see cref="Models.Channel.HasCustomBumper"/> flag and the probed duration; the
/// bytes live on disk where they belong.
/// </summary>
public class BumperService
{
    // 4 KiB header window is enough to probe an ftyp box: MP4 files place ftyp within the first few
    // hundred bytes typically. Keep the read small so uploads don't stall on validation.
    private const int HeaderSniffBytes = 4096;

    private readonly IMediaEncoder _encoder;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<BumperService> _logger;

    /// <summary>Initializes a new instance of the <see cref="BumperService"/> class.</summary>
    /// <param name="encoder">Media encoder, used to locate ffprobe.</param>
    /// <param name="paths">Application paths, used to place the bumpers directory.</param>
    /// <param name="logger">Logger.</param>
    public BumperService(IMediaEncoder encoder, IApplicationPaths paths, ILogger<BumperService> logger)
    {
        _encoder = encoder;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Gets the directory that holds every channel's uploaded bumper.</summary>
    public string BumpersRoot => Path.Combine(_paths.CachePath, "livechannels", "bumpers");

    /// <summary>Absolute path to a channel's uploaded bumper file (whether or not it exists on disk yet).</summary>
    /// <param name="channelId">Channel id.</param>
    /// <returns>The absolute path.</returns>
    public string PathFor(string channelId) => Path.Combine(BumpersRoot, SafeId(channelId) + ".mp4");

    /// <summary>Whether the channel has an uploaded bumper file on disk (non-empty).</summary>
    /// <param name="channelId">Channel id.</param>
    /// <returns>True when the file exists and has bytes.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Channel id is resolved to a config-stored id at the controller boundary and passes through SafeId here; the string reaching File.Exists cannot contain path separators or navigation segments.")]
    public bool Exists(string channelId)
    {
        var path = PathFor(channelId);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    /// <summary>Deletes the channel's uploaded bumper file if present. Silent no-op when absent.</summary>
    /// <param name="channelId">Channel id.</param>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Channel id is resolved to a config-stored id at the controller boundary and passes through SafeId here; the string reaching File.Delete cannot contain path separators or navigation segments.")]
    public void Delete(string channelId)
    {
        var path = PathFor(channelId);
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MyChannels: could not delete bumper file for channel {ChannelId}", channelId);
            }
        }
    }

    /// <summary>
    /// Saves the uploaded stream to the channel's bumper path. Overwrites any existing file. Validates the
    /// leading bytes look like an MP4 (ftyp box) before committing; anything else is refused so the
    /// streaming pipeline never gets handed a non-concat-friendly format.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <param name="input">Upload stream (typically the request body).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The saved file path on success, or <c>null</c> when the stream is empty or not an MP4.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Channel id is resolved to a config-stored id at the controller boundary and passes through SafeId here; the string reaching filesystem APIs cannot contain path separators or navigation segments.")]
    public async Task<string?> SaveAsync(string channelId, Stream input, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(BumpersRoot);
        var path = PathFor(channelId);
        var temp = path + ".upload";

        try
        {
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            if (new FileInfo(temp).Length == 0 || !LooksLikeMp4(temp))
            {
                File.Delete(temp);
                return null;
            }

            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { }
            }

            throw;
        }
    }

    /// <summary>
    /// Probes the file's duration via ffprobe. Returns 0 when ffprobe is unavailable or the file is unreadable
    /// — the caller decides whether that's fatal (upload rejection) or best-effort.
    /// </summary>
    /// <param name="path">Absolute path to a media file.</param>
    /// <returns>The duration in ticks, or 0 when unknown.</returns>
    public long ProbeDurationTicks(string path)
    {
        var ffprobe = _encoder.ProbePath;
        if (string.IsNullOrEmpty(ffprobe) || !File.Exists(path))
        {
            return 0;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("format=duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        startInfo.ArgumentList.Add(path);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return 0;
            }

            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                _logger.LogWarning("MyChannels: ffprobe timed out probing bumper duration for {Path}", path);
                return 0;
            }

            var stdout = process.StandardOutput.ReadToEnd().Trim();
            if (process.ExitCode != 0 || string.IsNullOrEmpty(stdout))
            {
                _logger.LogWarning("MyChannels: ffprobe failed on bumper {Path}: exit {Code}", path, process.ExitCode);
                return 0;
            }

            if (!double.TryParse(stdout, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            {
                return 0;
            }

            return (long)(seconds * TimeSpan.TicksPerSecond);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MyChannels: ffprobe threw probing bumper duration for {Path}", path);
            return 0;
        }
    }

    // Cheap MP4 sniff: a valid MP4 has an "ftyp" box somewhere near the start (bytes 4-7 of the first box).
    // We scan the first HeaderSniffBytes for the "ftyp" magic to be tolerant of the free/pnot boxes some
    // exporters emit ahead of ftyp. Not a full parser; if this passes we still hand the file to ffprobe
    // which will reject anything ffmpeg can't decode.
    private static bool LooksLikeMp4(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[Math.Min(HeaderSniffBytes, (int)Math.Min(stream.Length, HeaderSniffBytes))];
        var read = stream.Read(buffer, 0, buffer.Length);
        for (var i = 0; i + 4 <= read; i++)
        {
            if (buffer[i] == (byte)'f' && buffer[i + 1] == (byte)'t' && buffer[i + 2] == (byte)'y' && buffer[i + 3] == (byte)'p')
            {
                return true;
            }
        }

        return false;
    }

    // Same filesystem-safe id encoding AutoCardService uses. Ids are Guid-based so this is normally a no-op.
    private static string SafeId(string id)
    {
        var chars = id.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
