using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MyChannels.Models;
using Jellyfin.Plugin.MyChannels.Utilities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MyChannels.Services;

/// <summary>
/// Generates auto "Up Next" card MP4s inserted between programs on channels with a non-Off
/// <see cref="Channel.FillerMode"/>. Each card shows the next program's landscape backdrop with a dark
/// gradient panel on the right and the program title overlaid. A subtle Ken Burns zoom keeps pixels moving
/// during long cards so a static image doesn't burn in to a plasma or OLED.
/// </summary>
/// <remarks>
/// Cards are cached to <c><cache>/livechannels/cards/{channelId}/{itemId}-{durationMs}.mp4</c> and reused
/// while the underlying file exists. The plugin's schedule-cache clear does NOT purge these — cards are
/// bound to the program's item id, which is stable across schedule rebuilds. If a card's target file is
/// missing at stream time the pipeline just plays the next real program (missing filler is invisible).
/// </remarks>
public class AutoCardService
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Fps = 24;

    private readonly IMediaEncoder _encoder;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<AutoCardService> _logger;

    /// <summary>Initializes a new instance of the <see cref="AutoCardService"/> class.</summary>
    /// <param name="encoder">Media encoder, used to locate ffmpeg.</param>
    /// <param name="paths">Application paths, used to place the cache directory.</param>
    /// <param name="logger">Logger.</param>
    public AutoCardService(IMediaEncoder encoder, IApplicationPaths paths, ILogger<AutoCardService> logger)
    {
        _encoder = encoder;
        _paths = paths;
        _logger = logger;
    }

    private string CardsRoot => Path.Combine(_paths.CachePath, "livechannels", "cards");

    /// <summary>
    /// Returns the on-disk path of a card MP4 sized for the given duration, generating it once if it
    /// isn't already cached. Returns <c>null</c> when generation fails (missing ffmpeg, backdrop I/O error,
    /// etc.) — the caller should treat that as "no card, just play the next program directly."
    /// </summary>
    /// <param name="channelId">Channel id, used to scope the cache directory.</param>
    /// <param name="nextProgram">The program the card announces (its backdrop, title, series name).</param>
    /// <param name="duration">How long the card should play.</param>
    /// <returns>The absolute path to the card MP4, or <c>null</c> on failure.</returns>
    public string? EnsureCard(string channelId, ProgramEntry nextProgram, TimeSpan duration)
    {
        if (nextProgram is null || duration <= TimeSpan.Zero)
        {
            return null;
        }

        var chDir = Path.Combine(CardsRoot, SafeId(channelId));
        var fileName = nextProgram.ItemId.ToString("N", CultureInfo.InvariantCulture)
            + "-" + ((long)duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
            + ".mp4";
        var outPath = Path.Combine(chDir, fileName);

        if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
        {
            return outPath;
        }

        try
        {
            Directory.CreateDirectory(chDir);
            return Generate(nextProgram, duration, outPath) ? outPath : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MyChannels: could not generate Up Next card for {Title}", nextProgram.Title);
            return null;
        }
    }

    private bool Generate(ProgramEntry program, TimeSpan duration, string outPath)
    {
        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            return false;
        }

        var backdrop = program.GuideImagePath;
        var haveBackdrop = !string.IsNullOrEmpty(backdrop) && File.Exists(backdrop);
        var seconds = Math.Max(1.0, duration.TotalSeconds);

        var textFont = FontLocator.Find();
        var (upNext, mainTitle, subTitle) = SplitTitles(program);

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y"
        };

        if (haveBackdrop)
        {
            args.Add("-loop"); args.Add("1");
            args.Add("-t"); args.Add(seconds.ToString("F2", CultureInfo.InvariantCulture));
            args.Add("-i"); args.Add(backdrop!);
        }
        else
        {
            // Fallback background: solid dark blue. Matches the vibe of a "no artwork" TV slate.
            args.Add("-f"); args.Add("lavfi");
            args.Add("-t"); args.Add(seconds.ToString("F2", CultureInfo.InvariantCulture));
            args.Add("-i"); args.Add("color=c=0x1a1a2e:s=" + Width + "x" + Height + ":r=" + Fps);
        }

        // Silent stereo track so the concatenated stream keeps a valid audio path across the card.
        args.Add("-f"); args.Add("lavfi");
        args.Add("-t"); args.Add(seconds.ToString("F2", CultureInfo.InvariantCulture));
        args.Add("-i"); args.Add("anullsrc=r=48000:cl=stereo");

        args.Add("-filter_complex");
        args.Add(BuildFilter(haveBackdrop, seconds, textFont, upNext, mainTitle, subTitle));

        args.Add("-map"); args.Add("[out]");
        args.Add("-map"); args.Add("1:a");
        args.Add("-c:v"); args.Add("libx264");
        args.Add("-preset"); args.Add("veryfast");
        args.Add("-tune"); args.Add("stillimage");
        args.Add("-pix_fmt"); args.Add("yuv420p");
        args.Add("-r"); args.Add(Fps.ToString(CultureInfo.InvariantCulture));
        args.Add("-c:a"); args.Add("aac");
        args.Add("-b:a"); args.Add("128k");
        args.Add("-ar"); args.Add("48000");
        args.Add("-shortest");
        args.Add(outPath);

        return Run(ffmpeg, args, program.Title ?? "(card)");
    }

    // Builds the filter graph. Steps:
    //   1. Scale + centre-crop the backdrop to 1920x1080
    //   2. Ken Burns: slow zoom (1.00 to ~1.03 over the whole card) with a mild horizontal drift, so cards
    //      lasting minutes never present a still image (burn-in prevention on plasma/OLED)
    //   3. Dark gradient panel occupying the right ~45% of the frame (drawn as a semi-opaque box; a real
    //      linear gradient is expensive in ffmpeg and the flat box reads fine against most artwork)
    //   4. Text: "Up Next" label, then the main title, then optional subtitle (episode name for series)
    private static string BuildFilter(bool haveBackdrop, double seconds, string? textFont, string upNext, string mainTitle, string subTitle)
    {
        var sb = new StringBuilder();
        var frames = Math.Max(2, (int)Math.Round(seconds * Fps));

        // Scale to fill 1920x1080 preserving aspect. zoompan sees a still frame per iteration and generates
        // the pan/zoom animation frame by frame.
        sb.Append("[0:v]scale=");
        sb.Append(Width);
        sb.Append(':');
        sb.Append(Height);
        sb.Append(":force_original_aspect_ratio=increase,crop=");
        sb.Append(Width);
        sb.Append(':');
        sb.Append(Height);

        if (haveBackdrop)
        {
            // Subtle Ken Burns. `zoompan` treats the input as a stream of frames, so d=1 emits one output
            // frame per input frame with the current zoom/pan applied. `on` is output-frame count; we zoom
            // from 1.00 to ~1.03 over the whole card, drifting horizontally by 3% at the same time.
            sb.Append(",zoompan=z='1+0.03*on/");
            sb.Append(frames.ToString(CultureInfo.InvariantCulture));
            sb.Append("':x='iw*0.03*on/");
            sb.Append(frames.ToString(CultureInfo.InvariantCulture));
            sb.Append("':y='ih*0.01':d=1:s=");
            sb.Append(Width);
            sb.Append('x');
            sb.Append(Height);
            sb.Append(":fps=");
            sb.Append(Fps.ToString(CultureInfo.InvariantCulture));
        }

        // Dark panel on the right for text legibility.
        var panelX = (Width * 55 / 100).ToString(CultureInfo.InvariantCulture);
        var panelW = (Width * 45 / 100).ToString(CultureInfo.InvariantCulture);
        sb.Append(",drawbox=x=");
        sb.Append(panelX);
        sb.Append(":y=0:w=");
        sb.Append(panelW);
        sb.Append(":h=");
        sb.Append(Height);
        sb.Append(":color=black@0.65:t=fill");

        if (textFont is not null)
        {
            var font = textFont.Replace("\\", "/", StringComparison.Ordinal);
            var textX = (Width * 58 / 100).ToString(CultureInfo.InvariantCulture);

            // "Up Next" label
            sb.Append(",drawtext=fontfile='");
            sb.Append(font);
            sb.Append("':text='");
            sb.Append(SanitizeForDrawtext(upNext));
            sb.Append("':fontcolor=white@0.75:fontsize=36:x=");
            sb.Append(textX);
            sb.Append(":y=");
            sb.Append((Height * 38 / 100).ToString(CultureInfo.InvariantCulture));

            // Main title
            sb.Append(",drawtext=fontfile='");
            sb.Append(font);
            sb.Append("':text='");
            sb.Append(SanitizeForDrawtext(mainTitle));
            sb.Append("':fontcolor=white:fontsize=64:x=");
            sb.Append(textX);
            sb.Append(":y=");
            sb.Append((Height * 44 / 100).ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(subTitle))
            {
                sb.Append(",drawtext=fontfile='");
                sb.Append(font);
                sb.Append("':text='");
                sb.Append(SanitizeForDrawtext(subTitle));
                sb.Append("':fontcolor=white@0.9:fontsize=40:x=");
                sb.Append(textX);
                sb.Append(":y=");
                sb.Append((Height * 55 / 100).ToString(CultureInfo.InvariantCulture));
            }
        }

        sb.Append("[out]");
        return sb.ToString();
    }

    // Picks the "Up Next" label, main title, and optional subtitle from a program entry. For a series
    // episode: main = series name, sub = episode name. For a movie or standalone: main = title, sub = null.
    private static (string UpNext, string Main, string Sub) SplitTitles(ProgramEntry p)
    {
        const string label = "Up Next";
        if (!string.IsNullOrWhiteSpace(p.SeriesName))
        {
            var sub = string.IsNullOrWhiteSpace(p.RawName) ? string.Empty : p.RawName!;
            return (label, p.SeriesName!, sub);
        }

        return (label, p.Title ?? string.Empty, string.Empty);
    }

    private bool Run(string ffmpeg, List<string> args, string title)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            startInfo.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MyChannels: ffmpeg failed to start when generating Up Next card for {Title}", title);
            return false;
        }

        // Give ffmpeg up to a minute for a big backdrop; card generation is off the tune-in critical path.
        if (!process.WaitForExit(60_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best effort.
            }

            _logger.LogWarning("MyChannels: Up Next card generation timed out for {Title}", title);
            return false;
        }

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            _logger.LogWarning("MyChannels: ffmpeg exited {Code} generating Up Next card for {Title}: {Stderr}",
                process.ExitCode, title, stderr);
            return false;
        }

        return true;
    }

    // Filesystem-safe channel id used as a directory name. Ids are Guid-based so this is normally a no-op,
    // but the built-in Popular channel uses a string id we want to keep readable.
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

    // Same sanitiser as DefaultLogoService uses for drawtext text arguments.
    private static string SanitizeForDrawtext(string s)
        => s.Replace("\\", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("%", string.Empty, StringComparison.Ordinal)
            .Replace(":", " ", StringComparison.Ordinal);
}
