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

    /// <summary>Enables the NVEncC transcode engine.</summary>
    public const string Nvencc = "LIDSLABS_TRANSCODE_NVENCC";

    /// <summary>NVEncC QVBR quality target.</summary>
    public const string NvenccQvbr = "LIDSLABS_TRANSCODE_NVENCC_QVBR";

    /// <summary>Path to the NVEncC binary. Legacy: LIDSLABS_NVENCC_PATH.</summary>
    public const string NvenccPath = "LIDSLABS_TRANSCODE_NVENCC_PATH";

    /// <summary>Clients eligible for the Dolby Vision preservation path.</summary>
    public const string DvClients = "LIDSLABS_TRANSCODE_DV_CLIENTS";

    /// <summary>Ranked video codec preference, e.g. "hevc,h264".</summary>
    public const string PreferredVideoCodec = "LIDSLABS_TRANSCODE_PREFERRED_VIDEO_CODEC";

    /// <summary>Ranked audio ladder, e.g. "copy,aac@1152k,sidecar".</summary>
    public const string PreferredAudioCodec = "LIDSLABS_AUDIO_PREFERRED_CODEC";

    /// <summary>Enables substituting an in-file compatibility audio track.</summary>
    public const string AllowAudioCompat = "LIDSLABS_AUDIO_ALLOW_COMPAT";

    /// <summary>Maps each renamed lever to the pre-v0.4.0 name still honoured as an alias.</summary>
    private static readonly Dictionary<string, string> _legacyAliases = new(StringComparer.Ordinal)
    {
        [AllowHdr] = "LIDSLABS_ALLOW_HDR_TRANSCODE",
        [ForceHevcClients] = "LIDSLABS_FORCE_HEVC_CLIENTS",
        [SdrLadderClients] = "LIDSLABS_SDR_LADDER_CLIENTS",
        [NvenccPath] = "LIDSLABS_NVENCC_PATH",
    };

    private static readonly SortedDictionary<string, string> _legacyUsages = new(StringComparer.Ordinal);

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
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (!_legacyAliases.TryGetValue(name, out var legacy))
        {
            return null;
        }

        var legacyValue = Environment.GetEnvironmentVariable(legacy);
        if (string.IsNullOrEmpty(legacyValue))
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
