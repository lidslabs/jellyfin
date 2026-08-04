using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Reads peak luminance out of a Dolby Vision RPU for one window of a title.
/// </summary>
/// <remarks>
/// <para>
/// NO PIXEL IS EVER DECODED HERE. The cut is <c>-c:v copy</c>, mkvextract demuxes the elementary
/// stream, and dovi_tool reads the RPU NAL units out of it. That is the entire reason this path
/// costs a fraction of a second per probe point where decoding costs seconds, and it is what makes a
/// 34-point measurement of every title in the library affordable as a nightly task.
/// </para>
/// <para>
/// The three-process shape is not incidental complexity. ffmpeg's <c>-f hevc</c> muxer MANGLES
/// dual-layer Dolby Vision Profile 7 — which is most of this library — so the elementary stream has
/// to come out through mkvextract. Collapsing this into one ffmpeg invocation produces a file that
/// looks fine and yields wrong numbers on exactly the titles that matter most.
/// </para>
/// </remarks>
public static class LidslabsPeakProbe
{
    private const string MkvExtract = "mkvextract";

    private const string DoviTool = "dovi_tool";

    private const int ToolTimeoutMs = 120_000;

    /// <summary>
    /// Checks that the external tools this probe shells out to are present.
    /// </summary>
    /// <param name="missing">Names of the tools that could not be run.</param>
    /// <returns><c>true</c> when every tool responded.</returns>
    public static bool ToolsAvailable(out IReadOnlyList<string> missing)
    {
        var absent = new List<string>();

        foreach (var tool in new[] { MkvExtract, DoviTool })
        {
            if (!CanRun(tool))
            {
                absent.Add(tool);
            }
        }

        missing = absent;
        return absent.Count == 0;
    }

    /// <summary>
    /// Measures peak luminance for one window of a title.
    /// </summary>
    /// <param name="ffmpegPath">Path to the ffmpeg binary.</param>
    /// <param name="mediaPath">Absolute path to the media file.</param>
    /// <param name="timestampSeconds">Window start.</param>
    /// <param name="windowSeconds">Window length.</param>
    /// <param name="appPaths">Application paths, used for scratch space.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Peak nits, or 0 when the window yielded nothing usable.</returns>
    /// <remarks>
    /// A window that cannot be read returns 0 rather than throwing. Individual windows fail for
    /// ordinary reasons — a cut landing on a bad splice, a title whose profile dovi_tool declines —
    /// and the percentile over the surviving points absorbs a few losses without moving (34 points
    /// versus 33 differed by 0.3%). Wholesale failure is caught by the caller's minimum-points floor.
    /// </remarks>
    public static async Task<double> WindowPeakNitsAsync(
        string ffmpegPath,
        string mediaPath,
        double timestampSeconds,
        int windowSeconds,
        IApplicationPaths appPaths,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appPaths);

        var work = Path.Combine(
            appPaths.TempDirectory,
            "lidslabs-peak",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        try
        {
            Directory.CreateDirectory(work);

            var mkv = Path.Combine(work, "w.mkv");
            var hevc = Path.Combine(work, "w.hevc");
            var rpu = Path.Combine(work, "w.rpu");

            // Cut a window without re-encoding. -map 0:v:0 keeps a single track, which is what makes
            // the mkvextract track id below 0 by construction rather than by guess.
            //
            // RETRIED, because the first touch of a file on a spun-down disk returns ENOENT here and
            // succeeds moments later. Measured on this array: a cold open costs ~5 s of spin-up, and
            // the failure presents as "no such file" rather than as a timeout, which is exactly the
            // shape that gets misread as missing media. Without the retry a whole library's worth of
            // first-probe-per-title results would be lost on any drive that had gone to sleep.
            string? cut = null;
            for (var attempt = 0; attempt < 3 && cut is null; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3 * attempt), cancellationToken).ConfigureAwait(false);
                }

                cut = await RunAsync(
                    ffmpegPath,
                    [
                        "-hide_banner", "-loglevel", "error", "-y",
                        "-ss", timestampSeconds.ToString("F3", CultureInfo.InvariantCulture),
                        "-i", mediaPath,
                        "-t", windowSeconds.ToString(CultureInfo.InvariantCulture),
                        "-map", "0:v:0", "-c:v", "copy", "-an", "-sn", mkv
                    ],
                    cancellationToken).ConfigureAwait(false);
            }

            if (cut is null || !HasContent(mkv))
            {
                return 0;
            }

            var extract = await RunAsync(
                MkvExtract,
                ["tracks", mkv, "0:" + hevc],
                cancellationToken).ConfigureAwait(false);

            if (extract is null || !HasContent(hevc))
            {
                return 0;
            }

            // -m 2 rewrites the RPU as single-layer profile 8.1. It is first because it normalises
            // P7 dual-layer RPUs into something `info` always understands, and because it is the mode
            // the reference-scored measurements were taken with. L1 content-light metadata passes
            // through the conversion untouched, so the number is unaffected by the rewrite. -m 3 is
            // the P5 equivalent; plain extraction is the last resort so an unfamiliar profile still
            // yields something rather than nothing.
            string[][] modes = [["-m", "2"], ["-m", "3"], []];
            var haveRpu = false;

            foreach (var mode in modes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDelete(rpu);

                var argv = new List<string>(mode.Length + 4);
                argv.AddRange(mode);
                argv.Add("extract-rpu");
                argv.Add(hevc);
                argv.Add("-o");
                argv.Add(rpu);

                await RunAsync(DoviTool, argv.ToArray(), cancellationToken).ConfigureAwait(false);

                if (HasContent(rpu))
                {
                    haveRpu = true;
                    break;
                }
            }

            if (!haveRpu)
            {
                return 0;
            }

            var info = await RunAsync(
                DoviTool,
                ["info", "-i", rpu, "-s"],
                cancellationToken).ConfigureAwait(false);

            return info is null ? 0 : ParseMaxCll(info);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex)
        {
            logger?.LogDebug(ex, "lidslabs.peakScan: probe failed at {Timestamp}s of {Path}", timestampSeconds, mediaPath);
            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger?.LogDebug(ex, "lidslabs.peakScan: probe failed at {Timestamp}s of {Path}", timestampSeconds, mediaPath);
            return 0;
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    /// <summary>
    /// Measures peak luminance for one window by decoding it, for sources with no Dolby Vision RPU.
    /// </summary>
    /// <param name="ffmpegPath">Path to the ffmpeg binary.</param>
    /// <param name="mediaPath">Absolute path to the media file.</param>
    /// <param name="timestampSeconds">Window start.</param>
    /// <param name="windowSeconds">Window length.</param>
    /// <param name="width">Coded width.</param>
    /// <param name="height">Coded height.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Peak nits, or 0 when the window yielded nothing usable.</returns>
    /// <remarks>
    /// <para>
    /// This is the ONLY path available for HDR10 / HDR10+ / HLG, which have no RPU to read. It is
    /// deliberately NOT used for Dolby Vision: measured over 34 DV titles the two disagree by -49% to
    /// +152%, and scored against real SDR masters the RPU won.
    /// </para>
    /// <para>
    /// FULL CODED RESOLUTION, DELIBERATELY. Scaling down before measuring would average away the
    /// small bright regions that decide a peak, under-reporting silently — the failure would look
    /// like a slightly dim picture, not like an error.
    /// </para>
    /// <para>
    /// The raw luma plane for a 3-second 4K window is over a gigabyte, so ffmpeg writes to a pipe and
    /// frames are consumed one at a time into a single reused buffer. Peak memory is one frame
    /// regardless of window length, and nothing touches disk.
    /// </para>
    /// </remarks>
    public static async Task<double> DecodeWindowPeakNitsAsync(
        string ffmpegPath,
        string mediaPath,
        double timestampSeconds,
        int windowSeconds,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        var frameBytes = (long)width * height * 2; // p010le luma: one uint16 per sample
        if (frameBytes <= 0 || frameBytes > int.MaxValue)
        {
            return 0;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-hwaccel", "cuda", "-hwaccel_output_format", "cuda",
            "-ss", timestampSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", mediaPath,
            "-t", windowSeconds.ToString(CultureInfo.InvariantCulture),
            "-map", "0:v:0", "-an", "-sn", "-fps_mode", "passthrough",
            "-vf", "hwdownload,format=p010le,extractplanes=y",
            "-f", "rawvideo", "-"
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 0;
        }

        var drainStderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ToolTimeoutMs);

        var size = (int)frameBytes;
        var buffer = new byte[size];
        var maxCode = 0;
        var frames = 0;

        try
        {
            var stream = process.StandardOutput.BaseStream;
            while (true)
            {
                var filled = 0;
                while (filled < size)
                {
                    var read = await stream
                        .ReadAsync(buffer.AsMemory(filled, size - filled), timeout.Token)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    filled += read;
                }

                // A trailing partial frame is discarded rather than measured: a half-read frame's
                // tail is stale bytes from the previous one, which can only invent a peak.
                if (filled < size)
                {
                    break;
                }

                var samples = MemoryMarshal.Cast<byte, ushort>(buffer.AsSpan());
                for (var i = 0; i < samples.Length; i++)
                {
                    if (samples[i] > maxCode)
                    {
                        maxCode = samples[i];
                    }
                }

                frames++;
            }
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }
        catch (IOException)
        {
            KillQuietly(process);
            return 0;
        }
        finally
        {
            await drainStderr.ConfigureAwait(false);
        }

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
        }

        // NO SHIFT. p010le stores its 10 bits in the HIGH end of each 16-bit sample, and assuming
        // that survived `extractplanes` cost a whole scan pass: the filter emits gray10le, with the
        // code word in the LOW bits, so a >> 6 divides every sample by 64.
        //
        // What made that expensive to spot is the shape of the failure. A code of 531 became 8,
        // which is below the tv-range black floor of 64, so PqToNits clamped it to EXACTLY 0 -- the
        // same value this method returns for "the window could not be read". Every title reported
        // "no usable measurement" while ffmpeg was running perfectly and returning good data.
        //
        // Verified against the layout rather than the documentation: a real 4K HDR10 frame through
        // this exact filter chain peaks at 531, not 33984.
        return frames == 0 ? 0 : PqToNits(maxCode);
    }

    /// <summary>
    /// Inverse ST.2084: a 10-bit luma code word to cd/m².
    /// </summary>
    /// <param name="code">10-bit luma code word.</param>
    /// <returns>Luminance in nits.</returns>
    /// <remarks>
    /// TV-RANGE EXPANSION, AND WHY IT IS NOT OPTIONAL. The code is normalised as
    /// <c>(code - 64) / 876</c>, the ITU-R limited range for 10-bit video (16 and 235 shifted left
    /// by two). Dividing by 1023 instead — the obvious full-range reading — under-reports the result
    /// by roughly a third, and does so silently, since every number it produces still looks like a
    /// plausible nits figure.
    /// </remarks>
    internal static double PqToNits(int code)
    {
        const double M1 = 2610d / 16384d;
        const double M2 = 2523d / 4096d * 128d;
        const double C1 = 3424d / 4096d;
        const double C2 = 2413d / 4096d * 32d;
        const double C3 = 2392d / 4096d * 32d;
        const double LimitedBlack = 64d;
        const double LimitedWhite = 940d;

        var v = (code - LimitedBlack) / (LimitedWhite - LimitedBlack);
        v = Math.Clamp(v, 0d, 1d);

        var vp = Math.Pow(v, 1d / M2);
        var denominator = C2 - (C3 * vp);
        if (denominator <= 0d)
        {
            return 10000d;
        }

        return 10000d * Math.Pow(Math.Max((vp - C1) / denominator, 0d), 1d / M1);
    }

    /// <summary>
    /// Extracts the L1 MaxCLL figure from <c>dovi_tool info -s</c> output.
    /// </summary>
    /// <param name="output">Tool stdout.</param>
    /// <returns>Peak nits, or 0 when the line is absent.</returns>
    /// <remarks>
    /// The line looks like
    /// <c>RPU content light level (L1): MaxCLL: 444.79 nits, MaxFALL: 61.44 nits</c>.
    /// dovi_tool reports L1 MaxCLL as the MAXIMUM across the frames in the extracted RPU, so a single
    /// probe already aggregates its whole window — which is the quantity the caller's percentile is
    /// built to consume.
    /// </remarks>
    internal static double ParseMaxCll(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return 0;
        }

        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("L1", StringComparison.Ordinal)
                || !line.Contains("MaxCLL:", StringComparison.Ordinal))
            {
                continue;
            }

            var start = line.IndexOf("MaxCLL:", StringComparison.Ordinal) + "MaxCLL:".Length;
            var rest = line[start..].TrimStart();
            var end = 0;
            while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.'))
            {
                end++;
            }

            if (end > 0
                && double.TryParse(rest[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var nits))
            {
                return nits;
            }
        }

        return 0;
    }

    private static bool CanRun(string tool)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(tool)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.StartInfo.ArgumentList.Add("--version");
            process.Start();
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(10_000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<string?> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ToolTimeoutMs);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A tool that has stopped making progress must not wedge the whole scan. Kill the tree
            // and let the caller drop this window; the percentile tolerates the loss.
            KillQuietly(process);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        await stderr.ConfigureAwait(false);
        return process.ExitCode == 0 ? await stdout.ConfigureAwait(false) : null;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static bool HasContent(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
