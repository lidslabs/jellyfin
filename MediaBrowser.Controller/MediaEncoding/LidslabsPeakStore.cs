using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Per-title measured HDR peak luminance, in nits, used to drive SDR tonemapping.
/// </summary>
/// <remarks>
/// <para>
/// THE DEFECT THIS EXISTS TO FIX. Jellyfin emits <c>peak={TonemappingPeak}</c> into every
/// <c>tonemap_cuda</c> / <c>tonemap_opencl</c> filter unconditionally, from a single server-wide
/// setting. The unit is nits ÷ 10, so the stock default of 100 asserts "this title is mastered at
/// 1000 nits" about the entire library. Titles are not mastered at 1000 nits. Measured over 35
/// Dolby Vision titles the true figure ranges from roughly 220 to 1170, and the tonemapper is
/// being told the wrong number for nearly all of them.
/// </para>
/// <para>
/// Being told a peak that is too HIGH makes the tonemapper reserve headroom that the content never
/// uses: the whole image is compressed toward the bottom of the SDR range and the result is dim and
/// flat. Being told one that is too LOW clips the specular highlights — the sun off a car, a muzzle
/// flash, a window — into flat white. Those highlights are the thing HDR is FOR, so this is the
/// error that matters most.
/// </para>
/// <para>
/// Scored against real SDR Blu-ray masters of the same two titles, a measured per-title peak cut
/// specular error by 31% (Super Mario Galaxy) and 47% (Dune: Part Two) on peak-sensitive scenes,
/// with zero blown highlights introduced.
/// </para>
/// <para>
/// WHY A SIDECAR FILE AND NOT THE DATABASE. The value is derived, cheap to recompute, and useless
/// without the media it describes; it is a cache, not user data. A JSON file can be inspected,
/// diffed, hand-edited and deleted by an operator with no tooling, which matters for something whose
/// failure mode is a subtly wrong picture rather than an error. It also keeps the scan task free of
/// any schema migration.
/// </para>
/// <para>
/// WHY KEYED ON PATH AND SIZE. Path alone is not enough: re-ripping or upgrading a title leaves the
/// path identical while the content changes completely, and a stale peak would then be applied to a
/// different master with no way to notice. Size is a free, sufficient change signal here — a re-rip
/// that lands on the same byte count as its predecessor is not a real risk, and treating a
/// false-negative as "unscanned" costs one rescan.
/// </para>
/// </remarks>
public static class LidslabsPeakStore
{
    /// <summary>
    /// The lowest peak the filter can be told; measurements below it are clamped up to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filter clamps <c>peak</c> ≤ 10 to exactly 100 — output at peak 1, 5 and 10 is
    /// byte-identical to peak 100, and NOT to the omitted-peak output. This is worth stating
    /// precisely because the obvious reading is wrong: the value is not "ignored so the filter
    /// auto-detects", it is silently replaced by the same 1000-nit constant this whole class exists
    /// to stop asserting. Emitting a low peak is therefore worse than emitting nothing.
    /// </para>
    /// <para>
    /// The threshold sits at 11 rather than 10 because 11 is the first value the filter honours:
    /// 10.5, 11 and 12 all produce distinct output, while everything at or below 10 collapses onto
    /// the 1000-nit constant. It is therefore the closest a genuinely dim title can be represented,
    /// and a measurement under it is clamped here rather than thrown away — see
    /// <see cref="TryGetPeak"/> for why discarding was worse than clamping.
    /// </para>
    /// </remarks>
    public const int MinUsablePeakTenNits = 11;

    private const string StoreFileName = "lidslabs-peaks.json";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static readonly object _cacheLock = new();

    private static Dictionary<string, LidslabsPeakEntry>? _cache;

    private static DateTime _cacheStamp;

    private static long _cacheLength = -1;

    /// <summary>
    /// Gets the absolute path of the store file.
    /// </summary>
    /// <param name="appPaths">Application paths.</param>
    /// <returns>The store path.</returns>
    public static string GetStorePath(IApplicationPaths appPaths)
    {
        ArgumentNullException.ThrowIfNull(appPaths);

        return Path.Combine(appPaths.DataPath, StoreFileName);
    }

    /// <summary>
    /// Resolves the tonemap <c>peak</c> argument for a media file.
    /// </summary>
    /// <param name="appPaths">Application paths.</param>
    /// <param name="mediaPath">Absolute path of the media file being transcoded.</param>
    /// <param name="mediaSize">File size in bytes, or <c>null</c> when unknown.</param>
    /// <param name="peakTenNits">The resolved value, in the filter's nits ÷ 10 unit.</param>
    /// <returns><c>true</c> when a usable measured peak exists.</returns>
    /// <remarks>
    /// Returning false means "say nothing", NOT "fall back to the server default". The caller omits
    /// the <c>peak</c> token entirely in that case; see the call site in EncodingHelper for why.
    /// </remarks>
    public static bool TryGetPeak(IApplicationPaths appPaths, string? mediaPath, long? mediaSize, out int peakTenNits)
    {
        peakTenNits = 0;

        if (string.IsNullOrEmpty(mediaPath))
        {
            return false;
        }

        var entries = Load(appPaths);
        if (!entries.TryGetValue(mediaPath, out var entry))
        {
            return false;
        }

        // A size we recorded but that no longer matches means the file was replaced under us.
        // Prefer no peak over a peak measured from different content.
        if (entry.Size > 0 && mediaSize.HasValue && mediaSize.Value > 0 && entry.Size != mediaSize.Value)
        {
            return false;
        }

        // A MEASUREMENT BELOW WHAT THE FILTER CAN EXPRESS IS CLAMPED, NOT DISCARDED.
        //
        // This used to return false, on the reasoning that a title under ~110 nits is either not HDR
        // or mis-measured, and that omission would then let the filter read the stream's own
        // metadata. Both halves turned out to be wrong.
        //
        // Discarding does not produce omission. The caller's next branch catches any range that is
        // not exactly VideoRangeType.DOVI -- which includes every profile 7 and profile 8 title --
        // and emits the stock 1000-nit constant instead. The Wolf of Wall Street decodes to 94 nits
        // over 34 windows with 22 distinct values, a perfectly credible measurement, and was being
        // tonemapped as though it peaked at 1000: an order of magnitude out, in the direction that
        // darkens the picture.
        //
        // And omission would not have helped anyway. Measured on a DV title, omitting peak produced
        // the same YAVG and YMAX as peak=100, so "let the filter decide" resolves to the same
        // constant this class exists to stop asserting.
        //
        // The floor is a limit of the FILTER, not of the measurement: peak <= 10 is silently
        // replaced by 100 (byte-identical output, md5 011e4e41), while 11, 12, 15 and 20 are all
        // honoured and render distinctly. So the closest truth we can express for a 94-nit title is
        // the floor itself, not a number ten times too large.
        var value = entry.Nits / 10;
        peakTenNits = Math.Max(value, MinUsablePeakTenNits);
        return true;
    }

    /// <summary>
    /// Loads the store, reusing the cached copy while the file on disk is unchanged.
    /// </summary>
    /// <param name="appPaths">Application paths.</param>
    /// <returns>Entries keyed by absolute media path; empty when the store is missing or unreadable.</returns>
    /// <remarks>
    /// Invalidation is by (write time, length) rather than a file watcher: the store is written by a
    /// scheduled task at most a few times a day, the check is one stat per transcode start, and a
    /// watcher would add a lifetime to manage for no benefit at this rate.
    /// </remarks>
    public static IReadOnlyDictionary<string, LidslabsPeakEntry> Load(IApplicationPaths appPaths)
    {
        var path = GetStorePath(appPaths);

        lock (_cacheLock)
        {
            DateTime stamp;
            long length;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    _cache = new Dictionary<string, LidslabsPeakEntry>(StringComparer.Ordinal);
                    _cacheStamp = default;
                    _cacheLength = -1;
                    return _cache;
                }

                stamp = info.LastWriteTimeUtc;
                length = info.Length;
            }
            catch (IOException)
            {
                return _cache ?? new Dictionary<string, LidslabsPeakEntry>(StringComparer.Ordinal);
            }
            catch (UnauthorizedAccessException)
            {
                return _cache ?? new Dictionary<string, LidslabsPeakEntry>(StringComparer.Ordinal);
            }

            if (_cache is not null && stamp == _cacheStamp && length == _cacheLength)
            {
                return _cache;
            }

            // Anything wrong with the file yields an EMPTY store, never an exception: this runs on
            // the transcode start path, and a malformed cache must degrade to stock tonemapping
            // rather than fail playback.
            Dictionary<string, LidslabsPeakEntry> loaded;
            try
            {
                using var stream = File.OpenRead(path);
                var doc = JsonSerializer.Deserialize<LidslabsPeakDocument>(stream, _jsonOptions);
                loaded = doc?.Titles is null
                    ? new Dictionary<string, LidslabsPeakEntry>(StringComparer.Ordinal)
                    : new Dictionary<string, LidslabsPeakEntry>(doc.Titles, StringComparer.Ordinal);
            }
            catch (JsonException)
            {
                loaded = new Dictionary<string, LidslabsPeakEntry>(StringComparer.Ordinal);
            }
            catch (IOException)
            {
                loaded = new Dictionary<string, LidslabsPeakEntry>(StringComparer.Ordinal);
            }
            catch (UnauthorizedAccessException)
            {
                loaded = new Dictionary<string, LidslabsPeakEntry>(StringComparer.Ordinal);
            }

            _cache = loaded;
            _cacheStamp = stamp;
            _cacheLength = length;
            return _cache;
        }
    }

    /// <summary>
    /// Writes the store atomically and refreshes the cache.
    /// </summary>
    /// <param name="appPaths">Application paths.</param>
    /// <param name="entries">Entries keyed by absolute media path.</param>
    /// <param name="run">State of the scan pass doing the writing, or null when not scanning.</param>
    /// <remarks>
    /// Write-temp-then-move, because the reader is the transcode path: a partially written file
    /// would be read as a corrupt store and silently disable the fix across the whole library for
    /// as long as the scan takes.
    /// </remarks>
    public static void Save(
        IApplicationPaths appPaths,
        IReadOnlyDictionary<string, LidslabsPeakEntry> entries,
        LidslabsPeakRun? run = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var path = GetStorePath(appPaths);
        var temp = path + ".tmp";

        var document = new LidslabsPeakDocument
        {
            Version = 1,
            Run = run,
            Titles = new Dictionary<string, LidslabsPeakEntry>(entries, StringComparer.Ordinal),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, document, _jsonOptions);
        }

        File.Move(temp, path, true);

        lock (_cacheLock)
        {
            _cache = null;
            _cacheStamp = default;
            _cacheLength = -1;
        }
    }

    /// <summary>
    /// Formats a peak for the filter graph.
    /// </summary>
    /// <param name="peakTenNits">Value in the filter's nits ÷ 10 unit.</param>
    /// <returns>Invariant-culture decimal text.</returns>
    public static string Format(int peakTenNits)
        => peakTenNits.ToString(CultureInfo.InvariantCulture);
}
