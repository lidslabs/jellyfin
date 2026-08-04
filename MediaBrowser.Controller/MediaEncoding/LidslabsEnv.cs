using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Single entry point for reading lidslabs environment levers.
/// </summary>
/// <remarks>
/// <para>
/// Two problems this exists to solve.
/// </para>
/// <para>
/// FIRST — the flag parse was duplicated. <c>LIDSLABS_ALLOW_HDR_TRANSCODE</c> was parsed inline in
/// both EncodingHelper and MediaInfoController, and the two copies had to be kept in agreement by
/// hand; every new lever inherited the same hazard. There is now exactly one parse, so what counts
/// as "on" is defined in exactly one place.
/// </para>
/// <para>
/// SECOND — v0.4.0 renames the levers into the <c>LIDSLABS_AUDIO_*</c> / <c>LIDSLABS_TRANSCODE_*</c>
/// namespaces, and a rename is a breaking change for a running deployment: prod's compose sets the
/// old names, so a straight rename would silently disable every lever on the next pull. Levers that
/// fail <em>silently</em> are precisely how the v0.3.3 SDR-companion rung shipped dead for three
/// days. So each renamed lever keeps its old name as an alias — the new name wins when it carries a
/// value, the legacy name still works, and <see cref="ConsumeLegacyUsages"/> reports which legacy
/// names were actually read so startup can log a deprecation notice.
/// </para>
/// <para>
/// Legacy aliases are a migration aid, not a permanent contract; drop them in the minor after prod's
/// compose has been updated.
/// </para>
/// </remarks>
public static class LidslabsEnv
{
    /// <summary>Master toggle for HDR-preserving transcode. Legacy: LIDSLABS_ALLOW_HDR_TRANSCODE.</summary>
    public const string AllowHdr = "LIDSLABS_TRANSCODE_ALLOW_HDR";

    /// <summary>Clients forced to an HEVC transcode target. Legacy: LIDSLABS_FORCE_HEVC_CLIENTS.</summary>
    public const string ForceHevcClients = "LIDSLABS_TRANSCODE_FORCE_HEVC_CLIENTS";

    /// <summary>Clients given an SDR companion rung on the HDR master. Legacy: LIDSLABS_SDR_LADDER_CLIENTS.</summary>
    public const string SdrLadderClients = "LIDSLABS_TRANSCODE_SDR_LADDER_CLIENTS";

    // NO NVEncC LEVERS, AND NO DV-CLIENT LEVER — deliberately, see the class remarks.
    //
    // Earlier v0.4.0 drafts declared LIDSLABS_TRANSCODE_NVENCC{,_QVBR,_PATH} and
    // LIDSLABS_TRANSCODE_DV_CLIENTS here. The NVEncC engine was dropped (measurement showed
    // ffmpeg equals or beats it on every path we care about, and its libplacebo tonemapper
    // exits 1 on this host), and the DV preservation path is deferred past v0.4.0. Both sets
    // of constants outlived their implementations and became dead config surface.
    //
    // That is not harmless. A lever an operator can set that silently does nothing is the
    // exact shape of the v0.3.3 SDR-companion bug this class was written to prevent — the
    // deployment looks configured and behaves as if it is not. A lever ships in the same
    // commit as the code that reads it, or it does not ship.

    /// <summary>Ranked video codec preference, e.g. "hevc,h264".</summary>
    public const string PreferredVideoCodec = "LIDSLABS_TRANSCODE_PREFERRED_VIDEO_CODEC";

    /// <summary>
    /// Default for <see cref="PreferredVideoCodec"/>. AV1 is deliberately absent: its advantage
    /// is compression efficiency, which converts to visible quality only where bitrate is scarce.
    /// </summary>
    /// <remarks>
    /// Lives here rather than at a call site because there are now two — the PlaybackInfo
    /// negotiation and the forced transcode the remote bitrate cap produces — and an operator
    /// ranking that means one thing on one path and something else on the other is worse than
    /// no ranking at all.
    /// </remarks>
    public const string PreferredVideoCodecDefault = "hevc,h264";

    /// <summary>Ranked audio ladder, e.g. "copy,aac@1152k,sidecar".</summary>
    public const string PreferredAudioCodec = "LIDSLABS_AUDIO_PREFERRED_CODEC";

    // No LIDSLABS_AUDIO_MAX_CHANNELS. A draft of the 8-channel AAC fix added one so the
    // corrected build could be tested without shipping the change, and it was removed before
    // release for two reasons (Nick, 2026-08-03). It named a general property and delivered a
    // libfdk_aac-specific one, which is the shape of lever that reads as configured and is not;
    // and the underlying change is a BUG FIX, not a preference — AAC does 8 channels, we were
    // capping it to 6, and the negotiated ceiling already comes from the client's own
    // TranscodingMaxAudioChannels. A fix does not ship behind a switch: if it turns out wrong on
    // device it is reverted, not left as an env var nobody sets.

    // LIDSLABS_AUDIO_ALLOW_COMPAT was declared here and read by nothing. Removed for the same
    // reason as the NVEncC and DV-client levers: the compatibility-track substitution it named is
    // unconditional, so an operator could set it, see it accepted, and change nothing. The opt-out
    // it anticipated ships with the code that honours it or not at all.

    /// <summary>Maps each renamed lever to the pre-v0.4.0 name still honoured as an alias.</summary>
    private static readonly Dictionary<string, string> _legacyAliases = new(StringComparer.Ordinal)
    {
        [AllowHdr] = "LIDSLABS_ALLOW_HDR_TRANSCODE",
        [ForceHevcClients] = "LIDSLABS_FORCE_HEVC_CLIENTS",
        [SdrLadderClients] = "LIDSLABS_SDR_LADDER_CLIENTS",
    };

    private static readonly SortedDictionary<string, string> _legacyUsages = new(StringComparer.Ordinal);

    private static readonly SortedDictionary<string, string> _conflicts = new(StringComparer.Ordinal);

    private static readonly object _legacyLock = new();

    /// <summary>
    /// Reads a lever, preferring the v0.4.0 name and falling back to its legacy alias.
    /// </summary>
    /// <param name="name">The v0.4.0 lever name.</param>
    /// <returns>The raw value, or <c>null</c> when neither name carries one.</returns>
    public static string? Raw(string name)
    {
        // A present-but-empty new name counts as unset, not as an override: some
        // compose setups "clear" a variable by assigning an empty string, and
        // treating that as a deliberate silencing of the legacy value would
        // disable the lever in exactly the case this alias exists to protect.
        var value = Environment.GetEnvironmentVariable(name);
        var hasNew = !string.IsNullOrEmpty(value);

        if (!_legacyAliases.TryGetValue(name, out var legacy))
        {
            return hasNew ? value : null;
        }

        var legacyValue = Environment.GetEnvironmentVariable(legacy);
        var hasLegacy = !string.IsNullOrEmpty(legacyValue);

        // BOTH NAMES SET TO DIFFERENT VALUES IS A MISCONFIGURATION THAT MUST NOT BE SILENT.
        //
        // The realistic way to arrive here is not operator confusion, it is layering: an image sets
        // a default under the new name while the operator's compose still sets the old one. The new
        // name then wins on every lookup, and the operator's setting -- the one they wrote, can see,
        // and believe is in force -- is shadowed with nothing to indicate it. For the master HDR
        // toggle that silently disables the entire feature the deployment exists for.
        //
        // The precedence itself is correct and stays. What was missing was any way to notice, so the
        // conflict is recorded and reported alongside the deprecation notice at startup.
        if (hasNew && hasLegacy && !string.Equals(value, legacyValue, StringComparison.Ordinal))
        {
            lock (_legacyLock)
            {
                _conflicts[legacy] = name;
            }
        }

        if (hasNew)
        {
            return value;
        }

        if (!hasLegacy)
        {
            return null;
        }

        lock (_legacyLock)
        {
            _legacyUsages[legacy] = name;
        }

        return legacyValue;
    }

    /// <summary>
    /// Returns levers whose new and legacy names carry conflicting values, and clears the record.
    /// </summary>
    /// <returns>Legacy name mapped to the new name that is overriding it; empty when none conflict.</returns>
    public static IReadOnlyDictionary<string, string> ConsumeConflicts()
    {
        lock (_legacyLock)
        {
            if (_conflicts.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var snapshot = new Dictionary<string, string>(_conflicts, StringComparer.Ordinal);
            _conflicts.Clear();
            return snapshot;
        }
    }

    /// <summary>
    /// Evaluates a lever as a boolean flag.
    /// </summary>
    /// <param name="name">The v0.4.0 lever name.</param>
    /// <returns><c>true</c> when set to <c>"1"</c> or <c>"true"</c> (case-insensitive).</returns>
    public static bool Flag(string name)
    {
        var value = Raw(name);
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Evaluates a lever as a comma-separated list.
    /// </summary>
    /// <param name="name">The v0.4.0 lever name.</param>
    /// <param name="fallback">
    /// Parsed when the lever is unset. Pass the code default here so that an unset lever and a lever
    /// explicitly set to the default behave identically.
    /// </param>
    /// <returns>Trimmed, non-empty entries in the operator's order — order is significant for ranked levers.</returns>
    public static IReadOnlyList<string> List(string name, string? fallback = null)
    {
        var value = Raw(name);
        if (string.IsNullOrEmpty(value))
        {
            value = fallback;
        }

        if (string.IsNullOrEmpty(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Length > 0)
            .ToArray();
    }

    /// <summary>
    /// Returns the legacy lever names read so far and clears the record.
    /// </summary>
    /// <remarks>
    /// Return-and-clear keeps the deprecation notice to one line per boot rather than one per transcode.
    /// </remarks>
    /// <returns>Legacy name mapped to its replacement; empty when none were used.</returns>
    public static IReadOnlyDictionary<string, string> ConsumeLegacyUsages()
    {
        lock (_legacyLock)
        {
            if (_legacyUsages.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var snapshot = new Dictionary<string, string>(_legacyUsages, StringComparer.Ordinal);
            _legacyUsages.Clear();
            return snapshot;
        }
    }
}
