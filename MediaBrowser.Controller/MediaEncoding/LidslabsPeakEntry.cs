using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// One title's measured HDR peak, as persisted by <see cref="LidslabsPeakStore"/>.
/// </summary>
/// <remarks>
/// Everything past <see cref="Nits"/> is there to make a wrong number diagnosable. The failure this
/// guards against is not a crash but a picture that looks slightly off, which nobody reports and
/// nobody can bisect; recording HOW the number was obtained, from how many samples, and against
/// which file makes an implausible entry answerable by reading the store instead of re-deriving it.
/// </remarks>
public sealed class LidslabsPeakEntry
{
    /// <summary>
    /// Gets or sets the measured peak luminance in nits.
    /// </summary>
    /// <remarks>
    /// The 90th percentile of per-frame peak across the sampled points, NOT the maximum. The max is
    /// unstable under sampling density — Super Mario Galaxy measured 658 nits at 16 points and 968
    /// at 34, a 47% swing from nothing but sample count — because it is decided by whichever single
    /// brightest frame happened to be hit. Percentiles moved 0% across the same change. A statistic
    /// that depends on how hard you looked cannot be used to configure a filter.
    /// </remarks>
    public int Nits { get; set; }

    /// <summary>
    /// Gets or sets how the peak was measured: <c>rpu</c> or <c>decode</c>.
    /// </summary>
    /// <remarks>
    /// These are NOT interchangeable and the field exists to keep them distinguishable after the
    /// fact. Compared over 35 Dolby Vision titles at identical timestamps and statistic, decode
    /// differs from RPU by anywhere from -48% to +63%: they measure different things (the RPU
    /// carries the mastering intent, decode observes the delivered pixels) and agreeing on one title
    /// says nothing about the next.
    /// </remarks>
    public string? Method { get; set; }

    /// <summary>
    /// Gets or sets the number of probe points that produced a usable value.
    /// </summary>
    /// <remarks>
    /// The ACTUAL count, not the configured one. It previously recorded the constant, so every entry
    /// in the store read <c>34</c> whether the title yielded 34 usable windows or the bare minimum of
    /// 10 — which made the field useless for exactly the job it exists to do.
    /// </remarks>
    public int Points { get; set; }

    /// <summary>
    /// Gets or sets how many DISTINCT values the probe points produced.
    /// </summary>
    /// <remarks>
    /// The validity check on the measurement. A percentile over samples that are all the same number
    /// is not a measurement of anything, and without this field it is indistinguishable from a real
    /// one: Mickey 17 and The Wolf of Wall Street both stored <c>100 nits, method rpu, points 34</c>,
    /// and both carry an L1 that never changes across the entire film — 17,365 frames, one value.
    /// Their RPUs contain no per-shot grade at all, and the scan faithfully averaged a constant.
    /// <para>
    /// A count of 1 means placeholder metadata. Low counts are worth knowing about too but are NOT
    /// the same defect: Dune: Part Two carries 5 distinct values and Bumblebee 191, and the first is
    /// coarse where the second is a real curve.
    /// </para>
    /// </remarks>
    public int Distinct { get; set; }

    /// <summary>
    /// Gets or sets why this entry was not measured by the method its source would normally imply.
    /// </summary>
    /// <remarks>
    /// Currently only <c>rpu_flat</c>: a Dolby Vision title whose RPU carried no variation, so the
    /// measurement was re-taken by decoding actual pixels. Null on the normal path. Recorded rather
    /// than inferred because <see cref="Method"/> alone would then read <c>decode</c> on a DV title
    /// and look like a tooling failure instead of a deliberate fallback.
    /// </remarks>
    public string? Fallback { get; set; }

    /// <summary>
    /// Gets or sets the size in bytes of the file when scanned, used to detect replacement.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets when the scan ran.
    /// </summary>
    public DateTime? ScannedUtc { get; set; }
}
