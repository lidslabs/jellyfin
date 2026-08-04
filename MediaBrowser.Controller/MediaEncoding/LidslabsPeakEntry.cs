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
    /// Gets or sets the number of probe points sampled.
    /// </summary>
    public int Points { get; set; }

    /// <summary>
    /// Gets or sets the size in bytes of the file when scanned, used to detect replacement.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets when the scan ran.
    /// </summary>
    public DateTime? ScannedUtc { get; set; }
}
