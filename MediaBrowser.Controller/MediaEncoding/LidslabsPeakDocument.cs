using System.Collections.Generic;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// On-disk shape of the peak store written by <see cref="LidslabsPeakStore"/>.
/// </summary>
public sealed class LidslabsPeakDocument
{
    /// <summary>
    /// Gets or sets the schema version.
    /// </summary>
    /// <remarks>
    /// Present so a future format change can be detected rather than misread. An unrecognised
    /// version should be treated as an empty store -- the data is a rebuildable cache, so discarding
    /// it costs one rescan, while misinterpreting it silently mis-tonemaps the whole library.
    /// </remarks>
    public int Version { get; set; }

    /// <summary>
    /// Gets or sets the state of the most recent scan pass.
    /// </summary>
    /// <remarks>
    /// Written on every incremental save, so it describes a run that is still in progress or one that
    /// died, not only one that finished. That is the whole point: a pass over this library takes
    /// hours, and the run of 2026-08-02 aborted after 6h29m having measured 510 of 841 eligible
    /// titles. Nothing anywhere said so. The store looked like a completed scan whose selection
    /// predicate must be wrong, and that misreading cost an investigation before the task's own
    /// <c>Aborted</c> status turned it up.
    /// <para>
    /// The completion log line cannot cover this, because an aborted run never reaches it. Opening
    /// the file has to answer "did this finish?" on its own.
    /// </para>
    /// </remarks>
    public LidslabsPeakRun? Run { get; set; }

    /// <summary>
    /// Gets or sets the entries, keyed by absolute media path.
    /// </summary>
    public Dictionary<string, LidslabsPeakEntry>? Titles { get; set; }
}

/// <summary>
/// Progress of a scan pass, persisted alongside the entries.
/// </summary>
public sealed class LidslabsPeakRun
{
    /// <summary>
    /// Gets or sets when this pass started.
    /// </summary>
    public System.DateTime? StartedUtc { get; set; }

    /// <summary>
    /// Gets or sets when this pass last wrote the store.
    /// </summary>
    /// <remarks>
    /// On a completed pass this is the finish time. On an abandoned one it is when it stopped, which
    /// is the more useful of the two facts.
    /// </remarks>
    public System.DateTime? UpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets whether the pass ran to completion.
    /// </summary>
    public bool Completed { get; set; }

    /// <summary>
    /// Gets or sets how many titles this pass found eligible.
    /// </summary>
    public int Eligible { get; set; }

    /// <summary>
    /// Gets or sets how many titles this pass measured.
    /// </summary>
    public int Measured { get; set; }

    /// <summary>
    /// Gets or sets how many titles this pass skipped as already current.
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Gets or sets how many titles this pass failed to measure.
    /// </summary>
    public int Failed { get; set; }

    /// <summary>
    /// Gets or sets how many eligible titles this pass had not yet reached.
    /// </summary>
    /// <remarks>
    /// Non-zero with <see cref="Completed"/> false is the signature of an interrupted pass, and is
    /// the single number that would have made the aborted run self-evident.
    /// </remarks>
    public int Remaining { get; set; }
}
