using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Measures each HDR title's peak luminance and records it for the SDR tonemapper.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS RUNS AHEAD OF TIME RATHER THAN AT TRANSCODE START. A useful measurement needs ~34 probe
/// points spread across the runtime, which costs on the order of fifteen seconds per title. That is
/// invisible in a nightly task and intolerable in front of a play button, so the work is done once
/// and cached. It also means the answer does not vary with how busy the box was when playback began.
/// </para>
/// <para>
/// WHY THE RPU AND NOT THE PIXELS. There are two ways to measure a title's peak: read the Dolby
/// Vision RPU's L1 metadata, or decode frames and measure luma. They are not interchangeable —
/// compared over 34 titles at identical timestamps and statistic, decode differs from the RPU by
/// -49% to +152%, agrees within ±10% on only 29% of titles, and reads high on 71%.
/// </para>
/// <para>
/// Which is BETTER was settled against real SDR Blu-ray masters, scoring specular-highlight error.
/// The RPU-derived peak won on both titles with ground truth (-34.9% and -16.7% versus stock). The
/// decode-derived peak tied it on one title and, on the other, was BEATEN BY EMITTING NOTHING AT ALL
/// — it measured 998 nits where the RPU said 658, which merely reproduces the stock 1000-nit default
/// and captures none of the available win. The RPU path is also ~6× faster (it never touches a
/// pixel) and more robust: one title yielded zero usable decode points and a full 34 from the RPU.
/// </para>
/// <para>
/// The cost of that choice is two binaries in the image, <c>mkvextract</c> and <c>dovi_tool</c>. When
/// they are absent only the Dolby Vision titles are skipped — the decode path needs nothing but
/// ffmpeg, so the rest of the library is still measured.
/// </para>
/// <para>
/// EVERY HDR TITLE IS SCANNED, NOT ONLY DOLBY VISION, and the method is chosen per title: the RPU
/// where one exists, decoding where one does not. The defect being fixed is a single library-wide
/// peak applied to every HDR title, so scanning only the Dolby Vision half would leave the majority
/// of the library on the constant — fixing the symptom for 46% of titles and calling it done.
/// </para>
/// <para>
/// Decode is used ONLY where there is no RPU. Where both are available the RPU wins on measurement,
/// not preference. But the two candidate fallbacks for non-DV were also scored, against real SDR
/// masters with the RPU stripped so the titles behave as HDR10: letting the filter auto-detect was
/// <b>12% WORSE</b> than changing nothing on one title while better on the other — it resolves to
/// MaxCLL, a static label and a maximum — whereas a decode-derived p90 was never worse than stock
/// (-0.7% and -21.8%) and matched the RPU exactly on one. Never-worse is why decode ships here and
/// auto-detection does not.
/// </para>
/// </remarks>
public class LidslabsPeakScanTask : IScheduledTask
{
    /// <summary>
    /// Probe points per title.
    /// </summary>
    /// <remarks>
    /// Chosen by measuring the statistic's stability, not by taste. At 16 points Super Mario Galaxy's
    /// per-frame MAXIMUM read 658 nits and at 34 it read 968 — a 47% swing caused by nothing but
    /// sample count, because a single rare bright frame decides a maximum. The p90 this task records
    /// moved 0% across the same change. 34 is where the percentile had demonstrably settled.
    /// </remarks>
    private const int ProbePoints = 34;

    /// <summary>
    /// Seconds of content each probe point covers.
    /// </summary>
    /// <remarks>
    /// Must match <c>CLIP_S</c> in the host reference implementation
    /// (<c>scripts/dovi/peak_scan.py</c>). Every threshold in this file was derived from numbers
    /// that script produced; a container that samples a different window is not measuring the same
    /// quantity, and the disagreement would show up as a subtly different picture rather than as an
    /// error.
    /// </remarks>
    private const int WindowSeconds = 3;

    /// <summary>
    /// Percentile of per-window peak recorded as the title's peak.
    /// </summary>
    /// <remarks>
    /// NOT the maximum — see <see cref="ProbePoints"/>. A statistic that changes by half depending on
    /// how hard you looked cannot be used to configure a filter.
    /// </remarks>
    private const double Percentile = 90d;

    /// <summary>
    /// Minimum usable probe points before a title's measurement is trusted.
    /// </summary>
    /// <remarks>
    /// Transient dropouts barely move the percentile (34/34 versus 33/34 differed by 0.3%), so the
    /// risk this guards is not sampling noise but wholesale failure — one library title produced zero
    /// usable points. A handful of survivors would place the percentile essentially arbitrarily.
    /// </remarks>
    private const int MinPoints = 10;

    /// <summary>
    /// At or below this many distinct probe values, the source metadata carries no grade.
    /// </summary>
    /// <remarks>
    /// See the flat-L1 fallback in <c>MeasureAsync</c> for the measurements behind the value.
    /// </remarks>
    private const int FlatMetadataDistinctValues = 2;

    private const int QueryPageLimit = 100;

    private static readonly BaseItemKind[] _itemKinds = [BaseItemKind.Movie, BaseItemKind.Episode];

    private static readonly DtoOptions _dtoOptions = new(false);

    /// <summary>
    /// Video range types that carry a Dolby Vision RPU this task can read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>VideoRangeType.DOVI</c> ALONE IS NOT "this is Dolby Vision", and assuming it was made
    /// this task select nothing at all. Per <c>MediaStream.GetVideoColorRange</c>, bare
    /// <c>DOVI</c> is reachable only for profile 5, or profile 10 with a base-layer compatibility
    /// id of 0. Profile 7 types as <c>DOVIWithEL</c>; profile 8 types by its compatibility id into
    /// <c>DOVIWithHDR10</c> / <c>DOVIWithHLG</c> / <c>DOVIWithSDR</c>, and to
    /// <c>DOVIInvalid</c> for anything off-spec — which includes real, readable discs.
    /// </para>
    /// <para>
    /// The library that motivated this feature is 212 profile 7, 8 profile 8, and ZERO profile 5 or
    /// 10. So the original predicate matched 0 of 220 Dolby Vision titles, would have done so
    /// forever, and logged "no Dolby Vision titles found" as though that were an ordinary result.
    /// Every part of the chain behind it was correct and unreachable. This is the same shape as the
    /// v0.3.3 SDR companion rung and the v0.3.4 client matcher: a gate that never fires is
    /// indistinguishable from a gate with no work to do.
    /// </para>
    /// <para>
    /// <c>DOVIInvalid</c> is included deliberately. It means "off Dolby's spec", not "unreadable" —
    /// a profile 8 title with compatibility id 6 types as invalid and its RPU parses fine, which was
    /// confirmed by measuring one end to end.
    /// </para>
    /// </remarks>
    private static readonly VideoRangeType[] _dolbyVisionRanges =
    [
        VideoRangeType.DOVI,
        VideoRangeType.DOVIWithHDR10,
        VideoRangeType.DOVIWithHLG,
        VideoRangeType.DOVIWithSDR,
        VideoRangeType.DOVIWithEL,
        VideoRangeType.DOVIWithHDR10Plus,
        VideoRangeType.DOVIWithELHDR10Plus,
        VideoRangeType.DOVIInvalid,
    ];

    /// <summary>
    /// Every video range type this task will measure.
    /// </summary>
    /// <remarks>
    /// The Dolby Vision family plus plain HDR10, HDR10+ and HLG. Non-DV titles are measured by
    /// decoding rather than from an RPU, which they do not have.
    /// <para>
    /// SDR is absent because there is nothing to tonemap. Everything else that reaches the SDR
    /// tonemapper is here, which is the point: the defect being fixed is a single library-wide peak
    /// applied to every HDR title, and scanning only part of the library would leave most of it on
    /// the constant.
    /// </para>
    /// </remarks>
    private static readonly VideoRangeType[] _hdrRanges =
    [
        VideoRangeType.DOVI,
        VideoRangeType.DOVIWithHDR10,
        VideoRangeType.DOVIWithHLG,
        VideoRangeType.DOVIWithSDR,
        VideoRangeType.DOVIWithEL,
        VideoRangeType.DOVIWithHDR10Plus,
        VideoRangeType.DOVIWithELHDR10Plus,
        VideoRangeType.DOVIInvalid,
        VideoRangeType.HDR10,
        VideoRangeType.HDR10Plus,
        VideoRangeType.HLG,
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<LidslabsPeakScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LidslabsPeakScanTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{LidslabsPeakScanTask}"/> interface.</param>
    public LidslabsPeakScanTask(
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        IApplicationPaths appPaths,
        ILogger<LidslabsPeakScanTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Named for HDR, not Dolby Vision. It was "Scan Dolby Vision peak luminance" while the RPU was
    /// the only source it read, but it covers every HDR title — of 828 eligible in the reference
    /// library only 239 are DV, and the other 589 are measured by decode. The old name understated
    /// its runtime by more than 3x to anyone reading the task list before starting it.
    /// </remarks>
    public string Name => "Scan HDR peak luminance";

    /// <inheritdoc />
    public string Key => "LidslabsPeakScan";

    /// <inheritdoc />
    public string Description =>
        "Measures the peak luminance of each HDR title so HDR to SDR transcodes tonemap against the "
        + "title's real peak instead of a single library-wide assumption. Dolby Vision titles are read "
        + "from their RPU metadata where it carries a real per-shot grade, and decoded otherwise; "
        + "HDR10 and HLG titles are always decoded, which is slower. Titles already measured are "
        + "skipped, so routine runs only cover newly added media and a first full pass takes hours.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        // MISSING TOOLS NO LONGER DISABLE THE WHOLE SCAN. dovi_tool and mkvextract are needed only
        // for the Dolby Vision path; HDR10, HDR10+ and HLG are measured by decoding, which needs
        // nothing but ffmpeg. Stopping outright would give up on the larger half of the library over
        // a dependency it does not use.
        var rpuToolsPresent = LidslabsPeakProbe.ToolsAvailable(out var missing);
        if (!rpuToolsPresent)
        {
            // One line, then carry on. Repeating this per title would bury the cause in a few
            // hundred identical failures, which is how a missing binary gets misread as broken media.
            _logger.LogWarning(
                "lidslabs.peakScan: {Missing} not on PATH — Dolby Vision titles will be skipped. "
                + "Non-DV HDR titles are unaffected; they are measured by decoding.",
                string.Join(", ", missing));
        }

        var store = new Dictionary<string, LidslabsPeakEntry>(
            LidslabsPeakStore.Load(_appPaths),
            StringComparer.Ordinal);

        var candidates = GetHdrItems(cancellationToken, out var examined);
        var total = candidates.Count;
        if (total == 0)
        {
            // WARNING, and it reports the denominator, because "found nothing" was exactly how the
            // first version of this task hid a bug that made it match 0 of 220 Dolby Vision titles.
            // At Information, with no count, it read as a library that simply has no HDR in it. A
            // zero result against a non-empty library is a claim worth making falsifiable at a
            // glance.
            if (examined > 0)
            {
                _logger.LogWarning(
                    "lidslabs.peakScan: no HDR titles found among {Examined} library items. "
                    + "If this library does contain HDR, the selection predicate is wrong, "
                    + "not the library.",
                    examined);
            }
            else
            {
                _logger.LogInformation("lidslabs.peakScan: no library items to examine.");
            }

            progress.Report(100);
            return;
        }

        _logger.LogInformation(
            "lidslabs.peakScan: {Total} HDR titles of {Examined} library items.",
            total,
            examined);

        var scanned = 0;
        var skipped = 0;
        var failed = 0;
        var completed = 0;
        var dirty = false;

        // Run state travels with every incremental save, not just the final one, so a pass that is
        // killed part way still leaves an honest account of itself in the file.
        var startedUtc = DateTime.UtcNow;
        LidslabsPeakRun Snapshot(bool done) => new()
        {
            StartedUtc = startedUtc,
            UpdatedUtc = DateTime.UtcNow,
            Completed = done,
            Eligible = total,
            Measured = scanned,
            Skipped = skipped,
            Failed = failed,
            Remaining = Math.Max(0, total - completed),
        };

        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = item.Path;
            if (!string.IsNullOrEmpty(path))
            {
                var size = SafeLength(path);

                if (IsFresh(store, path, size))
                {
                    skipped++;
                }
                else
                {
                    var runtimeSeconds = item.RunTimeTicks.HasValue
                        ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds
                        : 0d;

                    var stream = GetHdrVideoStream(item);
                    var useRpu = stream is not null
                        && Array.IndexOf(_dolbyVisionRanges, stream.VideoRangeType) >= 0
                        && rpuToolsPresent;

                    var measurement = await MeasureAsync(path, runtimeSeconds, stream, useRpu, cancellationToken)
                        .ConfigureAwait(false);
                    if (measurement.Nits > 0)
                    {
                        store[path] = new LidslabsPeakEntry
                        {
                            Nits = measurement.Nits,
                            Method = measurement.Method,
                            Points = measurement.Points,
                            Distinct = measurement.Distinct,
                            Fallback = measurement.Fallback,
                            Size = size,
                            ScannedUtc = DateTime.UtcNow,
                        };
                        scanned++;
                        dirty = true;
                        _logger.LogInformation(
                            "lidslabs.peakScan: {Name} = {Nits} nits (peak={Peak}, {Method}, "
                            + "{Points} points, {Distinct} distinct{Fallback})",
                            item.Name,
                            measurement.Nits,
                            measurement.Nits / 10,
                            measurement.Method,
                            measurement.Points,
                            measurement.Distinct,
                            measurement.Fallback is null ? string.Empty : ", fallback=" + measurement.Fallback);
                    }
                    else
                    {
                        failed++;
                        _logger.LogInformation("lidslabs.peakScan: no usable measurement for {Path}", path);
                    }

                    // Persist as we go. A scan of a large library is long enough that a restart part
                    // way through is a real event, and losing an hour of measurements to it would
                    // make the task feel unreliable in exactly the situation it is most needed.
                    if (dirty && scanned % 10 == 0)
                    {
                        SaveQuietly(store, Snapshot(done: false));
                        dirty = false;
                    }
                }
            }

            completed++;
            progress.Report(100d * completed / total);
        }

        // Always write on the way out, even with nothing dirty, so the run block records that this
        // pass reached the end. An incremental save from mid-pass would otherwise be the last word
        // and a completed scan would be indistinguishable from an abandoned one.
        SaveQuietly(store, Snapshot(done: true));

        _logger.LogInformation(
            "lidslabs.peakScan: complete. {Scanned} measured, {Skipped} already current, {Failed} failed, {Total} considered.",
            scanned,
            skipped,
            failed,
            total);

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Daily. The work is incremental -- an unchanged title is a dictionary lookup, not a probe --
        // so a routine run costs nothing and newly added media is measured within a day without
        // anyone remembering to trigger it.
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }

    private static bool IsFresh(
        IReadOnlyDictionary<string, LidslabsPeakEntry> store,
        string path,
        long size)
    {
        if (!store.TryGetValue(path, out var entry) || entry.Nits <= 0)
        {
            return false;
        }

        // A size mismatch means the file was replaced -- a re-rip or an upgrade lands on the same
        // path with entirely different content, and inheriting the old peak would apply one master's
        // measurement to another with nothing to reveal it.
        return entry.Size <= 0 || size <= 0 || entry.Size == size;
    }

    private static long SafeLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// One title's measurement, with enough context to tell a real number from a fabricated one.
    /// </summary>
    /// <param name="Nits">The percentile peak, or 0 when no usable measurement was obtained.</param>
    /// <param name="Points">Probe points that produced a usable value.</param>
    /// <param name="Distinct">Distinct values among those points; 1 means the source is a constant.</param>
    /// <param name="Method">The method that produced <paramref name="Nits"/>.</param>
    /// <param name="Fallback">Why the method differs from the source's usual one, or null.</param>
    private readonly record struct PeakMeasurement(
        int Nits,
        int Points,
        int Distinct,
        string Method,
        string? Fallback);

    private async Task<PeakMeasurement> MeasureAsync(
        string path,
        double runtimeSeconds,
        MediaStream? stream,
        bool useRpu,
        CancellationToken cancellationToken)
    {
        var samples = await SampleAsync(path, runtimeSeconds, stream, useRpu, cancellationToken)
            .ConfigureAwait(false);

        var distinct = CountDistinct(samples);
        string? fallback = null;

        // A DOLBY VISION TITLE WHOSE L1 NEVER CHANGES HAS NO GRADE TO READ, and a percentile over a
        // constant is not a measurement — it just launders the constant into something that looks
        // measured. Swept across all 239 Dolby Vision titles in the reference library, 17 (7.1%)
        // carry a completely flat L1, and the constant they carry is usually PLAUSIBLE: Spectre and
        // 10 Cloverfield Lane both report 622 nits, Transformers: Age of Extinction 1555, Titanic
        // 200. Only two of the seventeen were conspicuous. Nothing in the store distinguished them
        // from a real measurement, which is why this check exists rather than an eyeball pass.
        //
        // TWO distinct values is the cut, not one. Verified against dense samples: Spectre holds one
        // value across 17,372 frames, 1917 holds two across 17,382, and both are equally unusable —
        // every percentile of 1917 lands on 630. A further 12 titles sit at exactly two. If an RPU
        // says only two different things about an entire film it is not describing a per-shot grade,
        // so decoding pixels is strictly more informative regardless of which side of the line the
        // title falls on. Above two the counts run continuously up into the hundreds (Bumblebee has
        // 191) with no natural gap, so a higher cut would start discarding real, if coarse, grades.
        //
        // Decode and RPU are NOT interchangeable in general — they disagree by -48% to +63% over 35
        // titles, because one reads mastering intent and the other reads delivered light — so this
        // is not a free substitution. Against metadata carrying no information it is still better.
        if (useRpu && samples.Count > 0 && distinct <= FlatMetadataDistinctValues)
        {
            _logger.LogInformation(
                "lidslabs.peakScan: RPU L1 carries no grade ({Points} points, {Distinct} distinct "
                + "value(s)) for {Path} — re-measuring by decode.",
                samples.Count,
                distinct,
                path);

            var decoded = await SampleAsync(path, runtimeSeconds, stream, useRpu: false, cancellationToken)
                .ConfigureAwait(false);

            // Only take the fallback if it actually produced something usable. A decode that fails
            // leaves the flat RPU result in place, which is still wrong but is at least recorded
            // with Distinct = 1 so the store says so.
            if (decoded.Count >= MinPoints)
            {
                samples = decoded;
                distinct = CountDistinct(samples);
                fallback = "rpu_flat";
                useRpu = false;
            }
        }

        var method = useRpu ? "rpu" : "decode";

        if (samples.Count < MinPoints)
        {
            return new PeakMeasurement(0, samples.Count, distinct, method, fallback);
        }

        samples.Sort();
        var nits = (int)Math.Round(PercentileOf(samples, Percentile), MidpointRounding.AwayFromZero);
        return new PeakMeasurement(nits, samples.Count, distinct, method, fallback);
    }

    private static int CountDistinct(List<double> samples)
    {
        var seen = new HashSet<double>();
        foreach (var s in samples)
        {
            seen.Add(s);
        }

        return seen.Count;
    }

    private async Task<List<double>> SampleAsync(
        string path,
        double runtimeSeconds,
        MediaStream? stream,
        bool useRpu,
        CancellationToken cancellationToken)
    {
        var samples = new List<double>(ProbePoints);

        foreach (var timestamp in ProbeTimestamps(runtimeSeconds, ProbePoints))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var value = useRpu
                ? await LidslabsPeakProbe
                    .WindowPeakNitsAsync(
                        _mediaEncoder.EncoderPath,
                        path,
                        timestamp,
                        WindowSeconds,
                        _appPaths,
                        _logger,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await LidslabsPeakProbe
                    .DecodeWindowPeakNitsAsync(
                        _mediaEncoder.EncoderPath,
                        path,
                        timestamp,
                        WindowSeconds,
                        stream?.Width ?? 0,
                        stream?.Height ?? 0,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (value > 0)
            {
                samples.Add(value);
            }
        }

        return samples;
    }

    /// <summary>
    /// Spreads probe points across the runtime, excluding both endpoints.
    /// </summary>
    /// <remarks>
    /// <c>duration * (i + 1) / (count + 1)</c>, which keeps the first and last samples a few percent
    /// inside the runtime so probes are not spent on the distributor logo or the black tail after the
    /// credits — neither is representative of the grade.
    /// <para>
    /// This is the host reference's <c>probe_points()</c> verbatim, and it is copied rather than
    /// improved on purpose. The percentile recorded here is only comparable with the measurements
    /// that justified every threshold in this file if it is taken at the same timestamps.
    /// </para>
    /// </remarks>
    private static IEnumerable<double> ProbeTimestamps(double runtimeSeconds, int count)
    {
        if (runtimeSeconds <= 0 || count <= 0)
        {
            yield break;
        }

        for (var i = 0; i < count; i++)
        {
            yield return Math.Round(runtimeSeconds * (i + 1) / (count + 1), 2);
        }
    }

    /// <summary>
    /// Linear-interpolated percentile of a pre-sorted sample.
    /// </summary>
    /// <remarks>
    /// Matches numpy's default so the values this task records are directly comparable with the
    /// host-side analysis the thresholds were derived from.
    /// </remarks>
    private static double PercentileOf(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var rank = (percentile / 100d) * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);

        return lower == upper
            ? sorted[lower]
            : sorted[lower] + ((sorted[upper] - sorted[lower]) * (rank - lower));
    }

    private List<BaseItem> GetHdrItems(CancellationToken cancellationToken, out int examined)
    {
        var results = new List<BaseItem>();
        var seen = 0;

        foreach (var library in _libraryManager.RootFolder.Children.ToList())
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IsVirtualItem = false,
                IncludeItemTypes = _itemKinds,
                DtoOptions = _dtoOptions,
                SourceTypes = [SourceType.Library],
                Limit = QueryPageLimit,
                Parent = library,
            };

            int previousCount;
            var startIndex = 0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                query.StartIndex = startIndex;
                var items = _libraryManager.GetItemList(query);

                foreach (var item in items)
                {
                    seen++;
                    if (GetHdrVideoStream(item) is not null)
                    {
                        results.Add(item);
                    }
                }

                startIndex += QueryPageLimit;
                previousCount = items.Count;
            }
            while (previousCount > 0);
        }

        examined = seen;
        return results;
    }

    /// <summary>
    /// Returns the item's HDR video stream, or <c>null</c> when it has none.
    /// </summary>
    /// <param name="item">Library item.</param>
    /// <returns>The stream, used both to select the item and to size its decode buffer.</returns>
    /// <remarks>
    /// Returns the STREAM rather than a bool because the decode path needs its coded width and
    /// height to size a frame, and re-fetching the media streams per title to get them would double
    /// the query cost for information already in hand.
    /// </remarks>
    private MediaStream? GetHdrVideoStream(BaseItem item)
    {
        try
        {
            return item.GetMediaStreams()
                .FirstOrDefault(stream => stream.Type == MediaStreamType.Video
                    && Array.IndexOf(_hdrRanges, stream.VideoRangeType) >= 0);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void SaveQuietly(
        IReadOnlyDictionary<string, LidslabsPeakEntry> store,
        LidslabsPeakRun? run = null)
    {
        try
        {
            LidslabsPeakStore.Save(_appPaths, store, run);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "lidslabs.peakScan: could not write the peak store");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "lidslabs.peakScan: could not write the peak store");
        }
    }
}
