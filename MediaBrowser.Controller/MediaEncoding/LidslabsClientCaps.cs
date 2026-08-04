using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dlna;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// What a client has actually told us it can decode, and which codecs can carry a given range.
/// </summary>
/// <remarks>
/// <para>
/// These rules were written for the ranked video-codec preference in <c>MediaInfoController</c>,
/// where the guiding principle is that <em>capability is never invented</em> — the operator may
/// rank codecs, but a codec the client has not advertised is never selected. The remote
/// bitrate-cap enforcement in <c>StreamingHelpers</c> needs the identical judgement on a
/// different code path, and a second hand-maintained copy of it is exactly the defect
/// <see cref="LidslabsEnv"/> exists to prevent. One home, two callers.
/// </para>
/// <para>
/// The distinction that makes this worth a shared class rather than a shared line of reasoning:
/// <b>what a client will direct-play and what it will accept as a transcode target are different
/// lists</b>, and reading the wrong one produces a black screen with nothing in the logs. Neptune
/// AV Player advertises HEVC in its DirectPlay profiles and its <em>only</em> video
/// TranscodingProfile is H.264 over HLS; the iOS app is the same shape. Sending either of them a
/// server-side HEVC transcode because "it plays HEVC" is a documented failure of this project,
/// not a hypothetical.
/// </para>
/// </remarks>
public static class LidslabsClientCaps
{
    /// <summary>
    /// Gets the video codecs a client will accept as a <b>transcode target</b>, from its
    /// TranscodingProfiles only.
    /// </summary>
    /// <remarks>
    /// DirectPlay and Codec profiles are deliberately excluded. They describe what the client can
    /// decode from a file it receives untouched, which is a wider set and the wrong question when
    /// the server is the one producing the stream.
    /// </remarks>
    /// <param name="profile">The client's device profile, or <c>null</c> if none is known.</param>
    /// <returns>The advertised transcode video codecs, lowercase, distinct; empty if unknown.</returns>
    public static string[] AdvertisedTranscodeVideoCodecs(DeviceProfile? profile)
    {
        if (profile?.TranscodingProfiles is null)
        {
            return Array.Empty<string>();
        }

        return profile.TranscodingProfiles
            .Where(t => t.Type == DlnaProfileType.Video)
            .SelectMany(t => SplitTrim(t.VideoCodec))
            .Select(c => c.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Gets the <see cref="VideoRangeType"/> values a client declares through its CodecProfiles.
    /// </summary>
    /// <remarks>
    /// An <b>empty</b> result means the client never narrowed the range at all — "unconstrained",
    /// not "SDR only". Those two are not the same answer and must not be collapsed: treating an
    /// absent condition as a refusal would strip HDR from every client that simply does not
    /// declare a range.
    /// </remarks>
    /// <param name="profile">The client's device profile, or <c>null</c> if none is known.</param>
    /// <returns>The declared range names, distinct and ordered; empty if none were declared.</returns>
    public static string[] DeclaredVideoRanges(DeviceProfile? profile)
    {
        if (profile?.CodecProfiles is null)
        {
            return Array.Empty<string>();
        }

        return profile.CodecProfiles
            .SelectMany(cp => cp.Conditions.Concat(cp.ApplyConditions))
            .Where(c => c.Property == ProfileConditionValue.VideoRangeType)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets a value indicating whether a client has declared that it accepts any HDR range.
    /// </summary>
    /// <param name="declaredRanges">The output of <see cref="DeclaredVideoRanges"/>.</param>
    /// <returns>
    /// <c>true</c> if any declared range is an HDR variant, <b>or</b> if nothing was declared at
    /// all (unconstrained). Only a client that declared ranges and listed no HDR one returns
    /// <c>false</c>.
    /// </returns>
    public static bool DeclaresHdrOrIsUnconstrained(IReadOnlyList<string> declaredRanges)
    {
        ArgumentNullException.ThrowIfNull(declaredRanges);

        if (declaredRanges.Count == 0)
        {
            return true;
        }

        return declaredRanges.Any(v =>
            v.Contains("HDR", StringComparison.OrdinalIgnoreCase)
            || v.Contains("HLG", StringComparison.OrdinalIgnoreCase)
            || v.Contains("DOVI", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets a predicate selecting codecs that can carry <paramref name="rangeType"/> without
    /// silently discarding it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dolby Vision → HEVC only. The RPU rides in HEVC NAL type 62; DV-in-AV1 is specified but
    /// essentially nothing renders it, so AV1 would discard the RPU. H.264 cannot carry HDR at all.
    /// </para>
    /// <para>
    /// HDR10/HLG → exclude H.264. It has no HDR10 static-metadata carriage, so an H.264 rung means
    /// a tonemapped SDR picture with no error logged anywhere — the Swiftfin washout.
    /// </para>
    /// <para>
    /// SDR/unknown → unrestricted. Unknown is deliberately permissive: refusing to rank on a range
    /// we could not compute would disable the caller on any item with odd probe output.
    /// </para>
    /// </remarks>
    /// <param name="rangeType">The source's video range.</param>
    /// <returns>A predicate over codec names.</returns>
    public static Func<string, bool> CodecPreservesRange(VideoRangeType rangeType)
    {
        var isDovi =
            rangeType == VideoRangeType.DOVI
            || rangeType == VideoRangeType.DOVIWithHDR10
            || rangeType == VideoRangeType.DOVIWithEL
            || rangeType == VideoRangeType.DOVIWithHDR10Plus
            || rangeType == VideoRangeType.DOVIWithELHDR10Plus
            || rangeType == VideoRangeType.DOVIWithHLG
            || rangeType == VideoRangeType.DOVIWithSDR
            || rangeType == VideoRangeType.DOVIInvalid;

        if (isDovi)
        {
            return codec =>
                string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase);
        }

        var isHdr =
            rangeType == VideoRangeType.HDR10
            || rangeType == VideoRangeType.HDR10Plus
            || rangeType == VideoRangeType.HLG;

        if (isHdr)
        {
            return codec =>
                !string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(codec, "avc", StringComparison.OrdinalIgnoreCase);
        }

        return _ => true;
    }

    /// <summary>
    /// Splits a comma-separated profile field, trimming and dropping empties.
    /// </summary>
    /// <param name="value">The raw field value.</param>
    /// <returns>The split values.</returns>
    public static string[] SplitTrim(string? value)
        => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
