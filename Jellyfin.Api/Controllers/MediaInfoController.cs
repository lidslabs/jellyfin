using System;
using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Api.Attributes;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.MediaInfoDtos;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The media info controller.
/// </summary>
[Route("")]
[Authorize]
public class MediaInfoController : BaseJellyfinApiController
{
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IDeviceManager _deviceManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediaInfoController> _logger;
    private readonly MediaInfoHelper _mediaInfoHelper;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaInfoController"/> class.
    /// </summary>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="deviceManager">Instance of the <see cref="IDeviceManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{MediaInfoController}"/> interface.</param>
    /// <param name="mediaInfoHelper">Instance of the <see cref="MediaInfoHelper"/>.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface..</param>
    public MediaInfoController(
        IMediaSourceManager mediaSourceManager,
        IDeviceManager deviceManager,
        ILibraryManager libraryManager,
        ILogger<MediaInfoController> logger,
        MediaInfoHelper mediaInfoHelper,
        IUserManager userManager)
    {
        _mediaSourceManager = mediaSourceManager;
        _deviceManager = deviceManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _mediaInfoHelper = mediaInfoHelper;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets live playback media info for an item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="userId">The user id.</param>
    /// <response code="200">Playback info returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="Task"/> containing a <see cref="PlaybackInfoResponse"/> with the playback information.</returns>
    [HttpGet("Items/{itemId}/PlaybackInfo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaybackInfoResponse>> GetPlaybackInfo([FromRoute, Required] Guid itemId, [FromQuery] Guid? userId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);
        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        return await _mediaInfoHelper.GetPlaybackInfo(item, user).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets live playback media info for an item.
    /// </summary>
    /// <remarks>
    /// For backwards compatibility parameters can be sent via Query or Body, with Query having higher precedence.
    /// Query parameters are obsolete.
    /// </remarks>
    /// <param name="itemId">The item id.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="maxStreamingBitrate">The maximum streaming bitrate.</param>
    /// <param name="startTimeTicks">The start time in ticks.</param>
    /// <param name="audioStreamIndex">The audio stream index.</param>
    /// <param name="subtitleStreamIndex">The subtitle stream index.</param>
    /// <param name="maxAudioChannels">The maximum number of audio channels.</param>
    /// <param name="mediaSourceId">The media source id.</param>
    /// <param name="liveStreamId">The livestream id.</param>
    /// <param name="autoOpenLiveStream">Whether to auto open the livestream.</param>
    /// <param name="enableDirectPlay">Whether to enable direct play. Default: true.</param>
    /// <param name="enableDirectStream">Whether to enable direct stream. Default: true.</param>
    /// <param name="enableTranscoding">Whether to enable transcoding. Default: true.</param>
    /// <param name="allowVideoStreamCopy">Whether to allow to copy the video stream. Default: true.</param>
    /// <param name="allowAudioStreamCopy">Whether to allow to copy the audio stream. Default: true.</param>
    /// <param name="playbackInfoDto">The playback info.</param>
    /// <response code="200">Playback info returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="Task"/> containing a <see cref="PlaybackInfoResponse"/> with the playback info.</returns>
    [HttpPost("Items/{itemId}/PlaybackInfo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlaybackInfoResponse>> GetPostedPlaybackInfo(
        [FromRoute, Required] Guid itemId,
        [FromQuery, ParameterObsolete] Guid? userId,
        [FromQuery, ParameterObsolete] int? maxStreamingBitrate,
        [FromQuery, ParameterObsolete] long? startTimeTicks,
        [FromQuery, ParameterObsolete] int? audioStreamIndex,
        [FromQuery, ParameterObsolete] int? subtitleStreamIndex,
        [FromQuery, ParameterObsolete] int? maxAudioChannels,
        [FromQuery, ParameterObsolete] string? mediaSourceId,
        [FromQuery, ParameterObsolete] string? liveStreamId,
        [FromQuery, ParameterObsolete] bool? autoOpenLiveStream,
        [FromQuery, ParameterObsolete] bool? enableDirectPlay,
        [FromQuery, ParameterObsolete] bool? enableDirectStream,
        [FromQuery, ParameterObsolete] bool? enableTranscoding,
        [FromQuery, ParameterObsolete] bool? allowVideoStreamCopy,
        [FromQuery, ParameterObsolete] bool? allowAudioStreamCopy,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PlaybackInfoDto? playbackInfoDto)
    {
        var profile = playbackInfoDto?.DeviceProfile;
        _logger.LogDebug("GetPostedPlaybackInfo profile: {@Profile}", profile);

        if (profile is null)
        {
            var caps = _deviceManager.GetCapabilities(User.GetDeviceId());
            if (caps is not null)
            {
                profile = caps.DeviceProfile;
            }
        }

        // lidslabs: Swiftfin (Apple TV, client "Swiftfin tvOS") gate — DIAGNOSTIC
        // ONLY as of 2026-07-21. Swiftfin's full release (v1.5) ships two player
        // engines — "Swiftfin" (VLCKit) and "Native" (Apple AVPlayer) — that post
        // DISTINCT DeviceProfiles but authenticate under the SAME client string
        // and both post DeviceProfile.Name=null, so the authenticated client app
        // name via User.GetClient() is the only stable handle on "this is Swiftfin
        // (either engine)". Patch 0017 re-pointed this gate to the renamed string;
        // patch 0018 then hung force-HEVC + a DV-P7 EL strip off it, but that
        // forced these clients onto a SERVER transcode they render HDR worse on
        // than their own direct-play (device round 2026-07-21: even a correct
        // HEVC-HDR10 transcode failed on AVPlayer and showed no HDR on VLCKit) — so
        // those two corrections were reverted. The gate is retained as the anchor
        // for the pending per-engine work (branch VLCKit vs AVPlayer by profile
        // signature) and to keep the "is this Swiftfin?" diagnostic below. Open
        // problem: why the players reject/mis-render our HDR transcode (required
        // for forced-subtitle titles, which MUST burn in and therefore transcode).
        // See DEBUG_LOG.md 2026-07-21.
        var lidslabsSwiftfinClient =
            profile is not null
            && string.Equals(User.GetClient(), "Swiftfin tvOS", StringComparison.OrdinalIgnoreCase);

        // lidslabs v0.3.2: forced-HEVC override — source-independent and gated on
        // its OWN enable. This is a codec *preference* lever, not part of HDR
        // enablement: it fires whenever a client in LIDSLABS_FORCE_HEVC_CLIENTS
        // advertises HEVC, for ANY source (SDR or HDR). It is deliberately NOT
        // gated on LIDSLABS_ALLOW_HDR_TRANSCODE — forcing HEVC on an SDR remux
        // (the original FORCE_HEVC intent) has nothing to do with the HDR master
        // toggle. Keeping the two independent means the base HDR-passthrough
        // feature (governed solely by LIDSLABS_ALLOW_HDR_TRANSCODE inside
        // IsHdrPassthroughMode) always fires on its own terms, and this lever can
        // never enable or disable it. The safeguard is unchanged: the override
        // never invents capability — LidslabsProfileClaimsHevc still requires the
        // client to advertise HEVC. Computed here (profile is resolved); the
        // TranscodingProfile rewrite happens below, once info.MediaSources exists.
        //
        // lidslabs 2026-07-23: Swiftfin can also opt into force-HEVC, but NOT via the
        // normal profile.Name CSV match (Swiftfin posts DeviceProfile.Name=null, so
        // LidslabsClientMatches can never see it) — it rides the authenticated-client
        // Swiftfin gate instead, enabled when "swiftfin" is listed in the same
        // LIDSLABS_FORCE_HEVC_CLIENTS env. Why this matters: a DV P7 (DOVIWithEL)
        // title Swiftfin transcodes otherwise falls to h264 + HDR->SDR tonemap
        // (Swiftfin's hevc profile excludes the raw P7 range), losing HDR. Forcing
        // HEVC makes it a clean, Apple-compliant HDR10-HEVC fMP4 stream (verified
        // 2026-07-23: hvc1 + colr BT.2020/PQ + mdcv + clli, re-decodes error-free;
        // the P7 EL block-addition notice is benign). Swiftfin already negotiates
        // Container=mp4 so it needs no fMP4 force. RESOLVED 2026-07-23: AVPlayer's
        // earlier VIDEO-RANGE=PQ rejection was an HDR-ONLY-master problem, not an HDR
        // problem — with the H.264 SDR companion rung now in the master (patch 0020),
        // Swiftfin plays HDR from start, seek and resume (all verified on device). The
        // old force-SDR workaround was removed in v0.3.3. Kept env-toggleable so
        // forced-HEVC on/off can still be A/B'd on one image; the earlier 0018
        // force-HEVC attempt also carried a DV-P7 EL-strip rework (now known
        // unnecessary) that confounded its result.
        var lidslabsSwiftfinForceHevc =
            lidslabsSwiftfinClient
            && (Environment.GetEnvironmentVariable("LIDSLABS_FORCE_HEVC_CLIENTS") ?? string.Empty)
                .Contains("swiftfin", StringComparison.OrdinalIgnoreCase);

        var lidslabsForceHevcEligible =
            profile is not null
            && (LidslabsClientMatches(
                    profile.Name,
                    Environment.GetEnvironmentVariable("LIDSLABS_FORCE_HEVC_CLIENTS"))
                || lidslabsSwiftfinForceHevc)
            && LidslabsProfileClaimsHevc(profile);

        // lidslabs v0.3: gate-decision diagnostic log. Logs at Debug, so it is
        // silent in a default (Information-level) build and emits nothing in
        // production. Raise the log level to Debug to diagnose a "the override
        // doesn't fire for client X" report in a single cycle — it dumps the
        // gate inputs (profile name, configured client list, HEVC capability)
        // and the eligibility result. See DECISIONS.md, "Diagnostic logging at
        // the eligibility point".
        _logger.LogDebug(
            "lidslabs.forceHevc gate: profile={ProfileName}, clients={Clients}, profileHevc={ProfileHevc}, eligible={Eligible}",
            profile?.Name,
            Environment.GetEnvironmentVariable("LIDSLABS_FORCE_HEVC_CLIENTS"),
            profile is not null && LidslabsProfileClaimsHevc(profile),
            lidslabsForceHevcEligible);

        // Swiftfin gate diagnostic (Debug; dormant at Information level). Raise the
        // level to Debug to confirm "is this request seen as Swiftfin?" in one
        // cycle.
        _logger.LogDebug(
            "lidslabs.swiftfin gate: client={Client}, eligible={Eligible}",
            User.GetClient(),
            lidslabsSwiftfinClient);

        // Copy params from posted body
        // TODO clean up when breaking API compatibility.
        userId ??= playbackInfoDto?.UserId;
        userId = RequestHelpers.GetUserId(User, userId);
        maxStreamingBitrate ??= playbackInfoDto?.MaxStreamingBitrate;
        startTimeTicks ??= playbackInfoDto?.StartTimeTicks;
        audioStreamIndex ??= playbackInfoDto?.AudioStreamIndex;
        subtitleStreamIndex ??= playbackInfoDto?.SubtitleStreamIndex;
        maxAudioChannels ??= playbackInfoDto?.MaxAudioChannels;
        mediaSourceId ??= playbackInfoDto?.MediaSourceId;
        liveStreamId ??= playbackInfoDto?.LiveStreamId;
        autoOpenLiveStream ??= playbackInfoDto?.AutoOpenLiveStream ?? false;
        enableDirectPlay ??= playbackInfoDto?.EnableDirectPlay ?? true;
        enableDirectStream ??= playbackInfoDto?.EnableDirectStream ?? true;
        enableTranscoding ??= playbackInfoDto?.EnableTranscoding ?? true;
        allowVideoStreamCopy ??= playbackInfoDto?.AllowVideoStreamCopy ?? true;
        allowAudioStreamCopy ??= playbackInfoDto?.AllowAudioStreamCopy ?? true;

        userId = RequestHelpers.GetUserId(User, userId);
        var user = userId.IsNullOrEmpty()
            ? null
            : _userManager.GetUserById(userId.Value);
        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var info = await _mediaInfoHelper.GetPlaybackInfo(
                item,
                user,
                mediaSourceId,
                liveStreamId)
            .ConfigureAwait(false);

        if (info.ErrorCode is not null)
        {
            return info;
        }

        if (profile is not null)
        {
            // lidslabs v0.3.2: if eligible, force HEVC to the front of the video
            // TranscodingProfiles so StreamBuilder picks HEVC over the client-listed
            // h264 — for ANY source, SDR or HDR (source-independent, matching the
            // FORCE_HEVC name; the old HDR-source gate silently no-op'd on SDR). On
            // an HDR source the v0.2 HDR-passthrough path then engages per
            // LIDSLABS_ALLOW_HDR_TRANSCODE; on an SDR source this is a clean HEVC-SDR
            // encode — IsHdrPassthroughMode bails on the source range (!= HDR) before
            // it looks at the output codec, so nothing wanders into HDR colour/tonemap
            // routing. Mutates the shared profile once per request (acceptable for
            // single-version library items).
            if (lidslabsForceHevcEligible)
            {
                foreach (var tp in profile.TranscodingProfiles.Where(t => t.Type == DlnaProfileType.Video))
                {
                    var codecs = LidslabsSplitTrim(tp.VideoCodec)
                        .Where(c => !string.Equals(c, "hevc", StringComparison.OrdinalIgnoreCase));
                    tp.VideoCodec = string.Join(",", new[] { "hevc" }.Concat(codecs));
                }

                _logger.LogInformation(
                    "lidslabs: forced HEVC override fired for profile={ProfileName}, ua={UserAgent}, item={ItemId}",
                    profile.Name,
                    Request.Headers.UserAgent.ToString(),
                    itemId);
            }

            // lidslabs v0.3.2 (patch 0009): Neptune AV Player is AVPlayer-on-tvOS,
            // the same renderer class as Swiftfin. When AV Player is the forced
            // client, also rewrite its video HLS container ts->mp4 so the forced
            // codec is delivered as fMP4 (CMAF), the Apple-native HLS path — the
            // same transport fix applied to Swiftfin above. This is a TRANSPORT
            // rewrite, codec-agnostic (the helper touches only Container, never the
            // codec): it holds for HEVC today and for any codec a future
            // preferred-codec lever forces (e.g. AV1), both undecodable in mpegts on
            // AVPlayer. Keyed on the neptune_av client match (single source of truth
            // for AV Player's name), not on "HEVC", so it stays correct if the force
            // ever generalizes from HEVC to a preferred codec. No new env var — fMP4
            // rides the same opt-in that forces the codec.
            //
            // INSURANCE, not the fix: the 2026-07-01 baseline showed AV Player's own
            // video TranscodingProfile already declares Container=mp4 (it negotiates
            // fMP4 natively), so LidslabsForceFmp4Hls is a no-op on that build and
            // this block never fires. It is retained to defend against a future
            // Neptune build that reverts its transcode container to ts, and to keep
            // the AV Player path symmetric with Swiftfin. General rule: force fMP4
            // whenever an Apple-TV AVPlayer client is forced to transcode over HLS.
            // Second AVPlayer adopter after Swiftfin; a third should promote both
            // call-sites to a shared code path (see FUTURE_REQUESTS.md).
            if (lidslabsForceHevcEligible
                && LidslabsClientMatches(profile.Name, "neptune_av")
                && LidslabsForceFmp4Hls(profile))
            {
                _logger.LogInformation(
                    "lidslabs: Neptune AV Player fMP4 HLS force applied (video TranscodingProfile container -> mp4) for profile={ProfileName}, item={ItemId}",
                    profile.Name,
                    itemId);
            }

            // lidslabs v0.3.3: the force-listed-clients-back-to-SDR lever (patches 0011
            // and 0014, env LIDSLABS_FORCE_SDR_CLIENTS) was REMOVED here. It existed for
            // AVPlayer-family tvOS clients (Neptune AV Player) that advertise HDR10 HEVC
            // yet stalled on an HDR-over-HLS master — they rejected an HDR-ONLY master's
            // VIDEO-RANGE=PQ and never requested a segment. The real cause was the master
            // advertising a single PQ variant; AVPlayer refuses to enter an HDR-only
            // ladder. The HDR-passthrough master now carries an H.264 SDR companion rung
            // (DynamicHlsHelper, patch 0020), so AVPlayer clients start from the SDR rung
            // and switch up to HDR on their own — Neptune AV now self-selects and plays
            // with no force-SDR needed. With the last force-SDR client gone, the lever and
            // its paired delivered-range SDR rewrite are dropped. The shared client matcher
            // (LidslabsClientMatches) stays — it still backs the forced-HEVC lever.

            // lidslabs 2026-07-21: the Swiftfin DV Profile 7 EL strip was REMOVED
            // here. It correctly forced P7 off direct-play, but for v1.5 that meant
            // a server HEVC re-encode of the base layer — which AVPlayer froze on
            // and VLCKit still rendered without HDR (DEBUG_LOG 2026-07-21). Swiftfin
            // now gets stock handling while the per-engine approach is worked out;
            // the lidslabsSwiftfinClient gate above is kept as the anchor for it.

            // set device specific data
            foreach (var mediaSource in info.MediaSources)
            {
                _mediaInfoHelper.SetDeviceSpecificData(
                    item,
                    mediaSource,
                    profile,
                    User,
                    maxStreamingBitrate ?? profile.MaxStreamingBitrate,
                    startTimeTicks ?? 0,
                    mediaSourceId ?? string.Empty,
                    audioStreamIndex,
                    subtitleStreamIndex,
                    maxAudioChannels,
                    info.PlaySessionId!,
                    userId ?? Guid.Empty,
                    enableDirectPlay.Value,
                    enableDirectStream.Value,
                    enableTranscoding.Value,
                    allowVideoStreamCopy.Value,
                    allowAudioStreamCopy.Value,
                    playbackInfoDto?.AlwaysBurnInSubtitleWhenTranscoding ?? false,
                    Request.HttpContext.GetNormalizedRemoteIP());
            }

            // lidslabs v0.3.2: describe the DELIVERED stream, not the source, for a
            // transcode of a DV/HDR source.
            // Jellyfin returns the source MediaStreams verbatim even when the source is
            // transcoded, so a client that keys its display pipeline off the reported
            // VideoRangeType (e.g. Moonfin's tvOS DisplayCriteriaManager, which maps any
            // "DOVI*" range to kCMVideoCodecType_DolbyVisionHEVC and any HDR range to a
            // PQ/HLG HDMI mode) selects a display mode for the SOURCE and then receives
            // bytes that don't match -> washed-out / wrong-gamut colors, plus a spurious
            // Dolby Vision banner for DV. The delivered-range case (HDR passthrough,
            // LIDSLABS_ALLOW_HDR_TRANSCODE): a DV source is passed through as clean
            // HDR10/HLG (no RPU, no -dolby_vision; the HLS master advertises
            // VIDEO-RANGE=PQ), so flatten the DV descriptors and the range resolves to
            // HDR10 (smpte2084) / HLG (arib-std-b67) from ColorTransfer, matching the
            // bytes. A native HDR10/HLG source already reports correctly and is left
            // untouched.
            //
            // VideoRangeType is a COMPUTED property (MediaStream.GetVideoColorRange)
            // derived from the DV fields + Hdr10PlusPresentFlag + ColorTransfer, so we
            // rewrite those. This mutates only the per-request response
            // clone (MediaInfoHelper deep-clones MediaSources before we touch them),
            // never library metadata. Scope: transcode only (TranscodingUrl set). The
            // play decision (SupportsDirectPlay / TranscodingUrl) is finalized above and
            // is NOT changed here, so no client can flip to direct-playing off the
            // rewritten range. Range only; a codec/profile rewrite is a separate follow-up.
            if (LidslabsHdrTranscodeEnabled())
            {
                foreach (var mediaSource in info.MediaSources)
                {
                    if (string.IsNullOrEmpty(mediaSource.TranscodingUrl))
                    {
                        continue;
                    }

                    var videoStream = mediaSource.MediaStreams?
                        .FirstOrDefault(s => s.Type == MediaStreamType.Video);
                    if (videoStream is null)
                    {
                        continue;
                    }

                    // Read the computed range BEFORE clearing any fields.
                    var rangeType = videoStream.VideoRangeType;

                    // HDR passthrough: only DV ranges whose HDR10/HLG base survives
                    // passthrough are eligible (bare DOVI profile 5 and DOVIWithSDR
                    // tonemap to SDR — leave them; native HDR10/HLG already correct).
                    var isPassthroughDovi =
                        rangeType == VideoRangeType.DOVIWithHDR10
                        || rangeType == VideoRangeType.DOVIWithEL
                        || rangeType == VideoRangeType.DOVIWithHDR10Plus
                        || rangeType == VideoRangeType.DOVIWithELHDR10Plus
                        || rangeType == VideoRangeType.DOVIWithHLG
                        || rangeType == VideoRangeType.DOVIInvalid;
                    if (!isPassthroughDovi)
                    {
                        continue;
                    }

                    LidslabsClearDoViDescriptors(videoStream);

                    _logger.LogInformation(
                        "lidslabs: PlaybackInfo delivered-range rewrite (DV {SourceRange} -> HDR10/HLG passthrough) for profile={ProfileName}, item={ItemId}",
                        rangeType,
                        profile.Name,
                        itemId);
                }
            }

            _mediaInfoHelper.SortMediaSources(info, maxStreamingBitrate);
        }

        if (autoOpenLiveStream.Value)
        {
            var mediaSource = string.IsNullOrWhiteSpace(mediaSourceId) ? info.MediaSources[0] : info.MediaSources.FirstOrDefault(i => string.Equals(i.Id, mediaSourceId, StringComparison.Ordinal));

            if (mediaSource is not null && mediaSource.RequiresOpening && string.IsNullOrWhiteSpace(mediaSource.LiveStreamId))
            {
                var openStreamResult = await _mediaInfoHelper.OpenMediaSource(
                    HttpContext,
                    new LiveStreamRequest
                    {
                        AudioStreamIndex = audioStreamIndex,
                        DeviceProfile = playbackInfoDto?.DeviceProfile,
                        EnableDirectPlay = enableDirectPlay.Value,
                        EnableDirectStream = enableDirectStream.Value,
                        ItemId = itemId,
                        MaxAudioChannels = maxAudioChannels,
                        MaxStreamingBitrate = maxStreamingBitrate,
                        PlaySessionId = info.PlaySessionId,
                        StartTimeTicks = startTimeTicks,
                        SubtitleStreamIndex = subtitleStreamIndex,
                        UserId = userId ?? Guid.Empty,
                        OpenToken = mediaSource.OpenToken,
                        AlwaysBurnInSubtitleWhenTranscoding = playbackInfoDto?.AlwaysBurnInSubtitleWhenTranscoding ?? false
                    }).ConfigureAwait(false);

                info.MediaSources = new[] { openStreamResult.MediaSource };
            }
        }

        return info;

        // lidslabs v0.3 helpers (static local functions — no instance capture).

        static string[] LidslabsSplitTrim(string? value)
            => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Maps user-facing client names (e.g. "neptune", "streamyfin") to the
        // internal profile.Name substring(s) that identify each Jellyfin client
        // mode. Keeps the env var ergonomic while letting us match the actual
        // DeviceProfile field that distinguishes client modes from one another
        // (Trident vs AV Player both ship in the Neptune app with the same UA
        // but different profile.Name values).
        //
        // To add a verified-working client mode: add a switch arm mapping its
        // friendly name to the distinguishing profile.Name substring. To
        // disable a previously-supported client without breaking existing
        // configs: comment out the arm — unmapped friendly names silently
        // return false, equivalent to "this client mode no longer gets the
        // override."
        static bool LidslabsClientMatches(string? profileName, string? csv)
        {
            if (string.IsNullOrWhiteSpace(profileName) || string.IsNullOrWhiteSpace(csv))
            {
                return false;
            }

            foreach (var friendly in LidslabsSplitTrim(csv))
            {
                var matched = friendly.ToLowerInvariant() switch
                {
                    "neptune" => profileName.Contains("Trident", StringComparison.OrdinalIgnoreCase),
                    "streamyfin" => profileName.Contains("1. MPV", StringComparison.OrdinalIgnoreCase),
                    // lidslabs v0.3.2 (patch 0009): Neptune AV Player — the app's
                    //     Apple-AVPlayer player mode. Name "Neptune tvOS" is a strict
                    //     prefix of Trident's "Neptune tvOS (Trident)", so match on
                    //     "Neptune tvOS" AND explicitly exclude "Trident" (single known
                    //     collision). AV Player: true && !false = true; Trident:
                    //     true && !true = false. The negation (vs Equals) keeps AV
                    //     Player matched if a future build appends an unrelated suffix
                    //     while Trident stays excluded. Both strings confirmed against a
                    //     live capture (neptune/151, app 0.1.6) — DEBUG_LOG 2026-07-01.
                    "neptune_av" => profileName.Contains("Neptune tvOS", StringComparison.OrdinalIgnoreCase)
                                    && !profileName.Contains("Trident", StringComparison.OrdinalIgnoreCase),
                    // lidslabs v0.3.3: the "moonfin" arm (patch 0014) was REMOVED. It was
                    //     added only so Moonfin could take the force-SDR lever, which
                    //     worked around an mpv HDR-render washout. That lever is gone
                    //     (see the force-SDR removal note above), and Moonfin now renders
                    //     HDR passthrough correctly on dev — no special-casing needed. If
                    //     the washout ever returns, re-add the arm; the history is in
                    //     DEBUG_LOG.md 2026-07-04 and .project/moonfin-hdr-mpv-washout-issue.md.
                    _ => false,
                };

                if (matched)
                {
                    return true;
                }
            }

            return false;
        }

        // lidslabs v0.3.2: strip the Dolby Vision descriptors from a
        // response-clone video MediaStream so its COMPUTED VideoRangeType stops
        // resolving to a DOVI* range. Used by the HDR-passthrough delivered-range
        // rewrite (DV -> HDR10/HLG). Clears the DV metadata
        // fields plus the dovi/dvh1/dvhe/dav1 CodecTag (GetVideoColorRange keys the DOVI
        // branch off both). Mutates only the per-request clone, never library metadata.
        static void LidslabsClearDoViDescriptors(MediaStream videoStream)
        {
            videoStream.DvProfile = null;
            videoStream.DvLevel = null;
            videoStream.RpuPresentFlag = null;
            videoStream.ElPresentFlag = null;
            videoStream.BlPresentFlag = null;
            videoStream.DvBlSignalCompatibilityId = null;
            videoStream.DvVersionMajor = null;
            videoStream.DvVersionMinor = null;
            if (string.Equals(videoStream.CodecTag, "dovi", StringComparison.OrdinalIgnoreCase)
                || string.Equals(videoStream.CodecTag, "dvh1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(videoStream.CodecTag, "dvhe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(videoStream.CodecTag, "dav1", StringComparison.OrdinalIgnoreCase))
            {
                videoStream.CodecTag = null;
            }
        }

        static bool LidslabsProfileClaimsHevc(DeviceProfile p)
            => p.DirectPlayProfiles.Any(dp => LidslabsSplitTrim(dp.VideoCodec).Contains("hevc", StringComparer.OrdinalIgnoreCase))
                || p.TranscodingProfiles.Any(tp => LidslabsSplitTrim(tp.VideoCodec).Contains("hevc", StringComparer.OrdinalIgnoreCase))
                || p.CodecProfiles.Any(cp => LidslabsSplitTrim(cp.Codec).Contains("hevc", StringComparer.OrdinalIgnoreCase));

        // lidslabs v0.3.2 (patch 0013): mirror the LIDSLABS_ALLOW_HDR_TRANSCODE gate
        // parse from EncodingHelper.IsHdrPassthroughMode so PlaybackInfo can tell whether
        // an eligible HDR/DV source will be delivered as HDR (passthrough) rather than
        // tonemapped to SDR. Same accepted values ("1" / "true") the transcode path uses,
        // so the delivered-range rewrite above never disagrees with what ffmpeg emits.
        static bool LidslabsHdrTranscodeEnabled()
        {
            var envFlag = Environment.GetEnvironmentVariable("LIDSLABS_ALLOW_HDR_TRANSCODE");
            if (string.IsNullOrEmpty(envFlag))
            {
                return false;
            }

            return string.Equals(envFlag, "1", StringComparison.Ordinal)
                || string.Equals(envFlag, "true", StringComparison.OrdinalIgnoreCase);
        }

        // Rewrites every video HLS TranscodingProfile's container to mp4 so the
        // resulting HLS stream is muxed as fMP4 (CMAF) instead of MPEG-TS. Apple
        // AVPlayer clients cannot decode HEVC-in-mpegts, so any forced remux for
        // such a client must use fMP4. Returns true if any profile was changed.
        // Idempotent (profiles already on mp4 are left as-is) and scoped to HLS
        // video profiles only — progressive/http and audio profiles are
        // untouched. Reusable: a future Apple-TV AVPlayer client (e.g. Neptune
        // AV Player) can call this from its own gate without re-deriving it.
        static bool LidslabsForceFmp4Hls(DeviceProfile p)
        {
            var changed = false;
            foreach (var tp in p.TranscodingProfiles.Where(t => t.Type == DlnaProfileType.Video
                                                                && t.Protocol == MediaStreamProtocol.hls))
            {
                if (!string.Equals(tp.Container, "mp4", StringComparison.OrdinalIgnoreCase))
                {
                    tp.Container = "mp4";
                    changed = true;
                }
            }

            return changed;
        }
    }

    /// <summary>
    /// Opens a media source.
    /// </summary>
    /// <param name="openToken">The open token.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="maxStreamingBitrate">The maximum streaming bitrate.</param>
    /// <param name="startTimeTicks">The start time in ticks.</param>
    /// <param name="audioStreamIndex">The audio stream index.</param>
    /// <param name="subtitleStreamIndex">The subtitle stream index.</param>
    /// <param name="maxAudioChannels">The maximum number of audio channels.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="openLiveStreamDto">The open live stream dto.</param>
    /// <param name="enableDirectPlay">Whether to enable direct play. Default: true.</param>
    /// <param name="enableDirectStream">Whether to enable direct stream. Default: true.</param>
    /// <param name="alwaysBurnInSubtitleWhenTranscoding">Always burn-in subtitle when transcoding.</param>
    /// <response code="200">Media source opened.</response>
    /// <returns>A <see cref="Task"/> containing a <see cref="LiveStreamResponse"/>.</returns>
    [HttpPost("LiveStreams/Open")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<LiveStreamResponse>> OpenLiveStream(
        [FromQuery] string? openToken,
        [FromQuery] Guid? userId,
        [FromQuery] string? playSessionId,
        [FromQuery] int? maxStreamingBitrate,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] Guid? itemId,
        [FromBody] OpenLiveStreamDto? openLiveStreamDto,
        [FromQuery] bool? enableDirectPlay,
        [FromQuery] bool? enableDirectStream,
        [FromQuery] bool? alwaysBurnInSubtitleWhenTranscoding)
    {
        userId ??= openLiveStreamDto?.UserId;
        userId = RequestHelpers.GetUserId(User, userId);
        var request = new LiveStreamRequest
        {
            OpenToken = openToken ?? openLiveStreamDto?.OpenToken,
            UserId = userId.Value,
            PlaySessionId = playSessionId ?? openLiveStreamDto?.PlaySessionId,
            MaxStreamingBitrate = maxStreamingBitrate ?? openLiveStreamDto?.MaxStreamingBitrate,
            StartTimeTicks = startTimeTicks ?? openLiveStreamDto?.StartTimeTicks,
            AudioStreamIndex = audioStreamIndex ?? openLiveStreamDto?.AudioStreamIndex,
            SubtitleStreamIndex = subtitleStreamIndex ?? openLiveStreamDto?.SubtitleStreamIndex,
            MaxAudioChannels = maxAudioChannels ?? openLiveStreamDto?.MaxAudioChannels,
            ItemId = itemId ?? openLiveStreamDto?.ItemId ?? Guid.Empty,
            DeviceProfile = openLiveStreamDto?.DeviceProfile,
            EnableDirectPlay = enableDirectPlay ?? openLiveStreamDto?.EnableDirectPlay ?? true,
            EnableDirectStream = enableDirectStream ?? openLiveStreamDto?.EnableDirectStream ?? true,
            DirectPlayProtocols = openLiveStreamDto?.DirectPlayProtocols ?? new[] { MediaProtocol.Http },
            AlwaysBurnInSubtitleWhenTranscoding = alwaysBurnInSubtitleWhenTranscoding ?? openLiveStreamDto?.AlwaysBurnInSubtitleWhenTranscoding ?? false
        };
        return await _mediaInfoHelper.OpenMediaSource(HttpContext, request).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes a media source.
    /// </summary>
    /// <param name="liveStreamId">The livestream id.</param>
    /// <response code="204">Livestream closed.</response>
    /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
    [HttpPost("LiveStreams/Close")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> CloseLiveStream([FromQuery, Required] string liveStreamId)
    {
        await _mediaSourceManager.CloseLiveStream(liveStreamId).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Tests the network with a request with the size of the bitrate.
    /// </summary>
    /// <param name="size">The bitrate. Defaults to 102400.</param>
    /// <response code="200">Test buffer returned.</response>
    /// <returns>A <see cref="FileResult"/> with specified bitrate.</returns>
    [HttpGet("Playback/BitrateTest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesFile(MediaTypeNames.Application.Octet)]
    public ActionResult GetBitrateTestBytes([FromQuery][Range(1, 100_000_000, ErrorMessage = "The requested size must be greater than or equal to {1} and less than or equal to {2}")] int size = 102400)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            Random.Shared.NextBytes(buffer);
            return File(buffer, MediaTypeNames.Application.Octet);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
