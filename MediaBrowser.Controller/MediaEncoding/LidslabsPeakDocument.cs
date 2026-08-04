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
    /// Gets or sets the entries, keyed by absolute media path.
    /// </summary>
    public Dictionary<string, LidslabsPeakEntry>? Titles { get; set; }
}
