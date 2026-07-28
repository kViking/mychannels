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
    private const int Fps = 60;

    // Panel geometry. Bottom-right corner, flush to frame edges. Tuned via testcards/tune.html so the
    // radial gradient fades from ~opaque at the panel's bottom-right through transparent at the top-left,
    // reading legibly over a wide range of backdrops without a hard boundary.
    private const int PanelWidth = 760;
    private const int PanelHeight = 340;
    private const int PanelX = Width - PanelWidth;
    private const int PanelY = Height - PanelHeight;

    // Radial gradient origin: offsets past the panel's bottom-right corner (so the origin sits down-and-right
    // of the panel, giving the darkest values in the bottom-right and fading upward-left).
    private const int GradientOriginXOffset = 421;
    private const int GradientOriginYOffset = 167;
    private const double GradientCurve = 0.4;
    // Top fade zone caps the alpha to 0 exactly at the panel's top edge, so the panel visually attaches to
    // the frame bottom without a hard top boundary regardless of backdrop.
    private const int GradientTopFadeZone = 133;

    // Text baseline: "Up Next" at 85px, main title at +55, subtitle at +130 (all measured from panel top).
    private const int TextYFirst = 85;
    private const int TextYMain = TextYFirst + 55;
    private const int TextYSub = TextYFirst + 130;

    // Timeline structural constants (seconds). Head/tail fades ALWAYS happen. Card cycles auto-fit the middle.
    private const double HeadFade = 0.5;
    private const double TailFade = 0.5;
    private const double LeadGap = 1.0;
    private const double TrailGap = 1.0;
    private const double CardIn = 0.5;
    private const double CardOut = 0.5;
    private const double CycleGapMin = 2.0;
    private const double MinHold = 2.5;

    private readonly IMediaEncoder _encoder;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<AutoCardService> _logger;

    // Tracks card outputs currently being generated in the background. Prevents duplicate ffmpeg invocations
    // for the same target file when multiple tune-ins hit Preheat for the same (program, duration) at once.
    // Keyed by the output path so ordering doesn't matter.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> InFlight = new();

    // One ffmpeg at a time in the background. On shared hosting we're a guest; card generation is best-effort
    // "nice to have" work and should never step on a viewer's live stream by contending for CPU.
    private static readonly SemaphoreSlim BackgroundSlot = new(1, 1);

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
    /// Returns the on-disk path of a card MP4 sized for the given duration IF it's already cached, else
    /// <c>null</c>. Fast-path only — pure cache lookup, no I/O beyond an existence check, never triggers
    /// generation. Callers wanting to schedule background gen should call <see cref="Preheat"/> separately.
    /// </summary>
    /// <param name="channelId">Channel id, used to scope the cache directory.</param>
    /// <param name="nextProgram">The program the card announces.</param>
    /// <param name="duration">How long the card would play.</param>
    /// <returns>The absolute path to the card MP4 if cached, or <c>null</c>.</returns>
    public string? EnsureCard(string channelId, ProgramEntry nextProgram, TimeSpan duration)
    {
        var path = TargetPath(channelId, nextProgram, duration);
        if (path is null)
        {
            return null;
        }

        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    /// <summary>
    /// Schedules a card to be generated in the background if it isn't already cached or already in flight.
    /// Runs at Idle process priority with a global concurrency of 1 (single ffmpeg at a time) so the plugin
    /// stays polite on shared hosting where a viewer's live stream must never contend with card generation
    /// for CPU.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <param name="nextProgram">The program the card announces.</param>
    /// <param name="duration">How long the card should play.</param>
    public void Preheat(string channelId, ProgramEntry nextProgram, TimeSpan duration)
    {
        var outPath = TargetPath(channelId, nextProgram, duration);
        if (outPath is null || (File.Exists(outPath) && new FileInfo(outPath).Length > 0))
        {
            return;
        }

        if (!InFlight.TryAdd(outPath, 0))
        {
            return;
        }

        var chDir = Path.GetDirectoryName(outPath)!;
        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(chDir);
                await BackgroundSlot.WaitAsync().ConfigureAwait(false);
                try
                {
                    Generate(nextProgram, duration, outPath);
                }
                finally
                {
                    BackgroundSlot.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MyChannels: background Up Next card generation failed for {Title}", nextProgram.Title);
            }
            finally
            {
                InFlight.TryRemove(outPath, out _);
            }
        });
    }

    private string? TargetPath(string channelId, ProgramEntry nextProgram, TimeSpan duration)
    {
        if (nextProgram is null || duration <= TimeSpan.Zero)
        {
            return null;
        }

        var chDir = Path.Combine(CardsRoot, SafeId(channelId));
        var fileName = nextProgram.ItemId.ToString("N", CultureInfo.InvariantCulture)
            + "-" + ((long)duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
            + ".mp4";
        return Path.Combine(chDir, fileName);
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
        args.Add(BuildFilter(seconds, textFont, upNext, mainTitle, subTitle));

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

    // Builds the filter graph. Structure (ported from testcards/gen-card.sh, tuned interactively):
    //
    //   [fade in]   backdrop fades from black                 (500 ms, structural)
    //   [lead]      backdrop only, breathing room             (1.0 s)
    //   [cycle 1]   panel slides in from bottom, holds, slides out
    //     [gap]     backdrop only                             (only if N > 1)
    //   [cycle N]   ...
    //   [trail]     backdrop only                             (1.0 s)
    //   [fade out]  backdrop fades to black                   (500 ms, structural)
    //
    // The head/tail fades ALWAYS happen. Number of cycles auto-fits: longer cards get more cycles and slightly
    // more hold-time per cycle, so a 2-minute card doesn't sit still for 90 seconds after a single reveal.
    // Panel is a radial gradient at the bottom-right corner (760x340), origin down-and-right of the panel so
    // the darkest values sit under the text and fade cleanly at the top edge without a hard boundary.
    private static string BuildFilter(double seconds, string? textFont, string upNext, string mainTitle, string subTitle)
    {
        var inv = CultureInfo.InvariantCulture;
        var (cycles, hold, cycleGap) = SolveTiming(seconds);
        var (overlayY, textAlpha) = BuildAnimationExprs(cycles, hold, cycleGap);
        var bgFadeOutStart = seconds - TailFade;

        // Radial gradient origin lives in PANEL coords, offset past the panel's bottom-right corner.
        // MaxReach = distance to the panel's opposite (top-left) corner (0, 0), so the gradient covers the
        // whole panel and dies at the far edge.
        var originX = PanelWidth + GradientOriginXOffset;
        var originY = PanelHeight + GradientOriginYOffset;
        var maxReach = Math.Sqrt((double)originX * originX + (double)originY * originY);

        var sb = new StringBuilder(2048);

        // ---- Background: scale + centre-crop to 1920x1080, fade in from black, fade out to black -----------
        sb.Append("[0:v]scale=").Append(Width).Append(':').Append(Height);
        sb.Append(":force_original_aspect_ratio=increase,crop=").Append(Width).Append(':').Append(Height);
        sb.Append(",fade=t=in:st=0:d=").Append(HeadFade.ToString("F3", inv));
        sb.Append(",fade=t=out:st=").Append(bgFadeOutStart.ToString("F3", inv))
            .Append(":d=").Append(TailFade.ToString("F3", inv));
        sb.Append("[bg];");

        // ---- Panel: transparent RGBA base, radial gradient painted via geq alpha ------------------------
        // The geq expression MUST escape commas as "\," inside a quoted arg because the outer filter graph
        // parser eats unescaped commas. `clip(255 * pow(max(0, 1 - dist/reach), curve) * clip(Y/topFade, 0, 1), 0, 255)`:
        // radial fall-off shaped by an exponent, capped near the top edge so the panel visually attaches to
        // the bottom of the frame with no hard line.
        sb.Append("color=c=black@0:s=").Append(PanelWidth).Append('x').Append(PanelHeight);
        sb.Append(":r=").Append(Fps).Append(":d=").Append(seconds.ToString("F3", inv));
        sb.Append(",format=rgba");
        sb.Append(",geq=r=0:g=0:b=0:a='clip(255 * pow(max(0\\, 1 - hypot(");
        sb.Append(originX).Append("-X\\, ").Append(originY).Append("-Y)/").Append(maxReach.ToString("F3", inv));
        sb.Append(")\\, ").Append(GradientCurve.ToString("F3", inv));
        sb.Append(") * clip(Y/").Append(GradientTopFadeZone).Append("\\, 0\\, 1), 0, 255)'");

        if (textFont is not null)
        {
            var font = textFont.Replace("\\", "/", StringComparison.Ordinal);

            // "Up Next" label — 32 pt, alpha follows the shared TEXT_ALPHA (fades with the panel + card cycle).
            sb.Append(",drawtext=fontfile='").Append(font).Append("':text='").Append(SanitizeForDrawtext(upNext));
            sb.Append("':fontcolor=white:fontsize=32:x=40:y=").Append(TextYFirst);
            sb.Append(":alpha='").Append(textAlpha).Append('\'');

            // Main title — 56 pt.
            sb.Append(",drawtext=fontfile='").Append(font).Append("':text='").Append(SanitizeForDrawtext(mainTitle));
            sb.Append("':fontcolor=white:fontsize=56:x=40:y=").Append(TextYMain);
            sb.Append(":alpha='").Append(textAlpha).Append('\'');

            if (!string.IsNullOrEmpty(subTitle))
            {
                // Subtitle (episode name for series) — 36 pt.
                sb.Append(",drawtext=fontfile='").Append(font).Append("':text='").Append(SanitizeForDrawtext(subTitle));
                sb.Append("':fontcolor=white:fontsize=36:x=40:y=").Append(TextYSub);
                sb.Append(":alpha='").Append(textAlpha).Append('\'');
            }
        }

        sb.Append("[panel];");

        // ---- Overlay panel onto background at animated Y (slides in from bottom, holds, slides out) --------
        sb.Append("[bg][panel]overlay=x=").Append(PanelX).Append(":y='").Append(overlayY).Append("'[out]");

        return sb.ToString();
    }

    // Auto-fits the largest N cycles that satisfy MinHold, then distributes remaining slack proportionally
    // to hold slots and gap slots. Total receivers = 2n-1 (n holds + n-1 gaps). Mirrors the Python solver in
    // testcards/gen-card.sh so what you see in the shell tuning tool is what the plugin will render.
    private static (int Cycles, double Hold, double Gap) SolveTiming(double seconds)
    {
        var middle = seconds - HeadFade - LeadGap - TrailGap - TailFade;
        var perCycleFixed = CardIn + CardOut;

        (double Hold, double Gap)? Fit(int n)
        {
            var baseline = n * (perCycleFixed + MinHold) + (n - 1) * CycleGapMin;
            var slack = middle - baseline;
            if (slack < 0)
            {
                return null;
            }

            var receivers = 2 * n - 1;
            var per = receivers > 0 ? slack / receivers : 0;
            var hold = MinHold + per;
            var gap = n > 1 ? CycleGapMin + per : 0;
            return (hold, gap);
        }

        var first = Fit(1);
        if (first is null)
        {
            // Won't fit even one cycle at MinHold — degrade by shortening the hold below floor rather than
            // dropping the card entirely.
            var hold = Math.Max(0.1, middle - perCycleFixed);
            return (1, hold, 0);
        }

        var (bestHold, bestGap) = first.Value;
        var bestN = 1;
        while (true)
        {
            var next = Fit(bestN + 1);
            if (next is null)
            {
                break;
            }

            bestN++;
            bestHold = next.Value.Hold;
            bestGap = next.Value.Gap;
        }

        return (bestN, bestHold, bestGap);
    }

    // Builds the ffmpeg overlay-y and text-alpha expressions as chained nested if() branches, one branch
    // triple per cycle: card slides in (Y ramps from H to PanelY over CardIn), holds (Y=PanelY), slides out
    // (Y ramps back to H over CardOut). Text alpha lags the panel by 100 ms on entry so the panel arrives
    // first. Outside all cycle windows the expressions fall through to the "hidden" defaults (Y=H, alpha=0).
    // The nested-if structure resolves shared time boundaries to the later-priority branch, which matches the
    // intended timeline (out > hold > in > default).
    private static (string OverlayY, string TextAlpha) BuildAnimationExprs(int cycles, double hold, double gap)
    {
        var inv = CultureInfo.InvariantCulture;
        string overlayY = Height.ToString(inv);
        string textAlpha = "0";

        for (var c = 0; c < cycles; c++)
        {
            var cycleStart = HeadFade + LeadGap + c * (CardIn + hold + CardOut + gap);
            var holdStart = cycleStart + CardIn;
            var outStart = holdStart + hold;
            var outEnd = outStart + CardOut;
            var textInStart = cycleStart + 0.1;

            // OVERLAY_Y: slide-in ramp -> hold -> slide-out ramp.
            overlayY = "if(between(t," + F(cycleStart) + "," + F(holdStart) + "),"
                + Height + "-(" + Height + "-" + PanelY + ")*(t-" + F(cycleStart) + ")/" + F(CardIn)
                + "," + overlayY + ")";
            overlayY = "if(between(t," + F(holdStart) + "," + F(outStart) + ")," + PanelY + "," + overlayY + ")";
            overlayY = "if(between(t," + F(outStart) + "," + F(outEnd) + "),"
                + PanelY + "+(" + Height + "-" + PanelY + ")*(t-" + F(outStart) + ")/" + F(CardOut)
                + "," + overlayY + ")";

            // TEXT_ALPHA: ramps 0->1 across (textInStart..holdStart), holds 1, ramps 1->0 across (outStart..outEnd).
            textAlpha = "if(between(t," + F(textInStart) + "," + F(holdStart) + "),(t-" + F(textInStart)
                + ")/(" + F(holdStart) + "-" + F(textInStart) + ")," + textAlpha + ")";
            textAlpha = "if(between(t," + F(holdStart) + "," + F(outStart) + "),1," + textAlpha + ")";
            textAlpha = "if(between(t," + F(outStart) + "," + F(outEnd) + "),1-(t-" + F(outStart)
                + ")/" + F(CardOut) + "," + textAlpha + ")";
        }

        return (overlayY, textAlpha);

        static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
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

            // Card generation is best-effort background work. Idle priority (nice ~19 on Linux) keeps a card
            // from stealing CPU from a live-stream encoder that a viewer is watching, which matters on shared
            // hosting where we don't own the box. Failure to set priority is non-fatal.
            try
            {
                process.PriorityClass = ProcessPriorityClass.Idle;
            }
            catch (Exception)
            {
                // Some platforms/OS configurations refuse the priority change; not worth logging every time.
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
