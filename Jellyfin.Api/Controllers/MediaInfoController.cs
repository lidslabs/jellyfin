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

        // lidslabs: Swiftfin (Apple TV, client "Swiftfin tvOS") gate. Swiftfin's
        // full release (v1.5, 2026-07) ships two player engines — "Swiftfin"
        // (VLCKit) and "Native" (Apple AVPlayer) — that post DISTINCT
        // DeviceProfiles but authenticate under the SAME client string and both
        // post DeviceProfile.Name=null. The authenticated client app name via
        // User.GetClient() — the same value SessionManager logs as "reported by
        // app" — is therefore the ONLY stable, collision-free handle on "this is
        // Swiftfin (either engine)"; the Name-keyed LidslabsClientMatches map
        // cannot see Swiftfin at all (Name is null).
        //
        // 2026-07-20/21 REGRESSION + RETUNE. v1.5 (a) renamed the client string
        // "Jellyfin tvOS" -> "Swiftfin tvOS" (the old gate silently went false,
        // disabling every Swiftfin correction the moment the app auto-updated),
        // and (b) rewrote both profiles so two former over-claims are GONE:
        // neither engine advertises truehd in audio direct-play anymore (a TrueHD
        // source natively selects+copies its AC3 compat track), and both default
        // their HLS transcode container to mp4/fMP4. The old TrueHD-strip and
        // fMP4-force corrections are dead against v1.5 and have been REMOVED. Two
        // over-claims remain and are corrected below: the DV Profile 7 direct-play
        // claim (EL strip) and the H264-default transcode that flattens HDR to SDR
        // (force-HEVC). Match the new string exactly; the old string is dropped
        // (the App Store auto-updates every install). See DEBUG_LOG.md 2026-07-21.
        var lidslabsSwiftfinClient =
            profile is not null
            && string.Equals(User.GetClient(), "Swiftfin tvOS", StringComparison.OrdinalIgnoreCase);

        // lidslabs v0.3.2: forced-HEVC override — source-independent codec
        // *preference* lever, not part of HDR enablement: it fires whenever an
        // eligible client advertises HEVC, for ANY source (SDR or HDR). It is
        // deliberately NOT gated on LIDSLABS_ALLOW_HDR_TRANSCODE — forcing HEVC on
        // an SDR remux has nothing to do with the HDR master toggle. Keeping the
        // two independent means the base HDR-passthrough feature (governed solely
        // by LIDSLABS_ALLOW_HDR_TRANSCODE inside IsHdrPassthroughMode) always fires
        // on its own terms, and this lever can never enable or disable it. The
        // safeguard is unchanged: the override never invents capability —
        // LidslabsProfileClaimsHevc still requires the client to advertise HEVC.
        //
        // 2026-07-21: Swiftfin is force-HEVC too, reached via the client gate
        // above rather than the LIDSLABS_FORCE_HEVC_CLIENTS name-map (Swiftfin
        // posts Name=null, so the map can never match it). Without this, Swiftfin's
        // profile lists h264 ahead of hevc and Jellyfin transcodes an HDR HEVC
        // source to h264 — which cannot carry HDR10, so the picture is tonemapped
        // to SDR (observed on both engines, DEBUG_LOG 2026-07-21). Forcing HEVC
        // keeps the HDR-passthrough path engaged so HDR survives the transcode.
        // Computed here (profile is resolved); the TranscodingProfile rewrite
        // happens below, once info.MediaSources exists.
        var lidslabsForceHevcEligible =
            profile is not null
            && LidslabsProfileClaimsHevc(profile)
            && (LidslabsClientMatches(
                    profile.Name,
                    Environment.GetEnvironmentVariable("LIDSLABS_FORCE_HEVC_CLIENTS"))
                || lidslabsSwiftfinClient);

        // lidslabs v0.3: gate-decision diagnostic log. Logs at Debug, so it is
        // silent in a default (Information-level) build and emits nothing in
        // production. Raise the log level to Debug to diagnose a "the override
        // doesn't fire for client X" report in a single cycle — it dumps the
        // gate inputs (profile name, configured client list, HEVC capability, the
        // Swiftfin client gate) and the eligibility result. See DECISIONS.md,
        // "Diagnostic logging at the eligibility point".
        _logger.LogDebug(
            "lidslabs.forceHevc gate: profile={ProfileName}, clients={Clients}, profileHevc={ProfileHevc}, swiftfin={Swiftfin}, eligible={Eligible}",
            profile?.Name,
            Environment.GetEnvironmentVariable("LIDSLABS_FORCE_HEVC_CLIENTS"),
            profile is not null && LidslabsProfileClaimsHevc(profile),
            lidslabsSwiftfinClient,
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

            // lidslabs v0.3.2 (patch 0011): force listed clients back to SDR. Some
            // AVPlayer-family tvOS clients (Neptune AV Player) advertise HDR10 HEVC
            // but cannot ingest an HDR-over-HLS stream: they reject the master
            // playlist's VIDEO-RANGE=PQ and never request a segment, so the screen
            // stays black. The global HDR-passthrough path
            // (LIDSLABS_ALLOW_HDR_TRANSCODE) hands HDR to every client regardless of
            // whether it asked for it, which is what breaks them. Rather than match
            // these clients by User-Agent at transcode time — where AV Player and
            // Trident are indistinguishable (same app UA) — decide HERE at
            // PlaybackInfo, where DeviceProfile.Name gives the collision-safe
            // delineation ("Neptune tvOS" vs "Neptune tvOS (Trident)"), reusing the
            // same LidslabsClientMatches map as the forced-HEVC lever. For a matched
            // client we PREPEND a hevc CodecProfile pinning the transcode target range
            // to SDR (Equals VideoRangeType=SDR). Prepend (not append) so it takes
            // highest priority in StreamBuilder's reversed codec-profile walk and
            // wins over any HDR10 range the client also declares. StreamBuilder then
            // emits &VideoRangeType=SDR on the transcode URL; the paired guard in
            // EncodingHelper.IsHdrPassthroughMode honors that requested SDR range and
            // drops out of HDR passthrough, flipping the HLS master VIDEO-RANGE and
            // the ffmpeg tonemap together. The forced HEVC codec is preserved — this
            // is a colour-range downgrade (HDR->SDR tonemap), NOT a codec change, so
            // efficiency is retained. Runtime-tunable via LIDSLABS_FORCE_SDR_CLIENTS
            // (add/remove a client with a compose edit + restart, no image rebuild).
            // Inert on SDR sources: the Equals-SDR condition is already satisfied, so
            // nothing is forced and direct-play/copy is unaffected. Idempotent:
            // skipped if this hevc Equals-SDR pin is already present.
            var lidslabsForceSdrEligible =
                LidslabsClientMatches(
                    profile.Name,
                    Environment.GetEnvironmentVariable("LIDSLABS_FORCE_SDR_CLIENTS"));

            // Gate-decision diagnostic (Debug; dormant at Information level, like the
            // forced-HEVC gate above). Raise the level to Debug to confirm whether a
            // client is seen as force-SDR-eligible in a single cycle.
            _logger.LogDebug(
                "lidslabs.forceSdr gate: profile={ProfileName}, clients={Clients}, eligible={Eligible}",
                profile.Name,
                Environment.GetEnvironmentVariable("LIDSLABS_FORCE_SDR_CLIENTS"),
                lidslabsForceSdrEligible);

            if (lidslabsForceSdrEligible)
            {
                var alreadyForcedSdr = profile.CodecProfiles
                    .Where(cp => cp.Type == CodecType.Video
                                 && LidslabsSplitTrim(cp.Codec).Contains("hevc", StringComparer.OrdinalIgnoreCase))
                    .Any(cp => cp.Conditions.Any(c =>
                        c.Property == ProfileConditionValue.VideoRangeType
                        && c.Condition == ProfileConditionType.Equals
                        && string.Equals(c.Value, "SDR", StringComparison.OrdinalIgnoreCase)));

                if (!alreadyForcedSdr)
                {
                    var forceSdr = new CodecProfile
                    {
                        Type = CodecType.Video,
                        Codec = "hevc",
                        Conditions = new[]
                        {
                            new ProfileCondition(
                                ProfileConditionType.Equals,
                                ProfileConditionValue.VideoRangeType,
                                "SDR",
                                false),
                        },
                    };

                    profile.CodecProfiles = new[] { forceSdr }.Concat(profile.CodecProfiles).ToArray();

                    _logger.LogInformation(
                        "lidslabs: forced-SDR override applied (prepended hevc VideoRangeType=SDR) for profile={ProfileName}, item={ItemId}",
                        profile.Name,
                        itemId);
                }
            }

            // lidslabs: Swiftfin over-claims Dolby Vision Profile 7 (dual-layer:
            // VideoRangeType DOVIWithEL / DOVIWithELHDR10Plus) direct-play. Neither
            // engine can render the enhancement layer: VLCKit software-decodes it
            // but never drives the TV's HDR handshake, and AVPlayer (tvOS supports
            // DV P5/P8 only, never P7) either shows the HDR10 base by luck or
            // hard-fails. v1.5's profiles now DECLARE a hevc VideoRangeType
            // allow-list — but it still INCLUDES DOVIWithELHDR10Plus on BOTH
            // engines, so the raw dual-layer stream is handed through (capture
            // 2026-07-21). The pre-v1.5 strip injected an EL-free allow-list only
            // when NO restriction existed and skipped when one did — exactly the
            // wrong branch now that v1.5 declares a permissive one.
            //
            // Remove the EL/dual-layer range tokens from every hevc VideoRangeType
            // condition the profile declares. A P7 source then fails the hevc
            // CodecProfile, so StreamBuilder.CanStreamCopyVideo copies the video
            // with the upstream RemoveDovi bitstream filter (dovi_rpu=strip) and
            // delivers the clean HDR10 base — HDR engages, NO re-encode. Single-
            // layer DV P5/P8 (DOVI, DOVIWithHDR10/HLG/SDR) and HDR10/HLG/SDR carry
            // no EL, stay in the list, and keep direct-playing. Unified across both
            // engines (Name is null, so we cannot and need not branch by engine).
            // Fallback: if the profile declares NO hevc VideoRangeType condition at
            // all, append an explicit EL-free allow-list (the pre-v1.5 behavior),
            // so the correction still holds if a future build drops its declaration.
            // VideoRangeType values are '|'-delimited (not comma), so they are split
            // directly here rather than via the comma-based LidslabsSplitTrim.
            if (lidslabsSwiftfinClient)
            {
                var elRanges = new[] { "DOVIWithEL", "DOVIWithELHDR10Plus" };

                var hevcRangeConditions = profile.CodecProfiles
                    .Where(cp => cp.Type == CodecType.Video
                                 && LidslabsSplitTrim(cp.Codec).Contains("hevc", StringComparer.OrdinalIgnoreCase))
                    .SelectMany(cp => cp.Conditions)
                    .Where(c => c.Property == ProfileConditionValue.VideoRangeType)
                    .ToArray();

                var correctedEl = false;
                foreach (var cond in hevcRangeConditions)
                {
                    var ranges = cond.Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var kept = ranges
                        .Where(r => !elRanges.Contains(r, StringComparer.OrdinalIgnoreCase))
                        .ToArray();

                    if (kept.Length != ranges.Length)
                    {
                        cond.Value = string.Join("|", kept);
                        correctedEl = true;
                    }
                }

                if (hevcRangeConditions.Length == 0)
                {
                    const string lidslabsAppleTvRangeTypes =
                        "SDR|HDR10|HLG|DOVI|DOVIWithHDR10|DOVIWithHLG|DOVIWithSDR|DOVIWithHDR10Plus|HDR10Plus";

                    profile.CodecProfiles = profile.CodecProfiles.Append(new CodecProfile
                    {
                        Type = CodecType.Video,
                        Codec = "hevc",
                        Conditions = new[]
                        {
                            new ProfileCondition(
                                ProfileConditionType.EqualsAny,
                                ProfileConditionValue.VideoRangeType,
                                lidslabsAppleTvRangeTypes,
                                false),
                        },
                    }).ToArray();
                    correctedEl = true;
                }

                if (correctedEl)
                {
                    _logger.LogInformation(
                        "lidslabs: Swiftfin DV Profile 7 strip applied (removed EL ranges from hevc VideoRangeType) for client={Client}, item={ItemId}",
                        User.GetClient(),
                        itemId);
                }
            }

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
            // Dolby Vision banner for DV. There are two delivered-range cases, keyed on
            // what the transcode actually emits:
            //
            //   * force-SDR eligible (LIDSLABS_FORCE_SDR_CLIENTS): the HDR/DV source is
            //     tonemapped to SDR (bt.709). Report SDR and clear the DV descriptors so
            //     the client does NOT engage DV/HDR — no re-engaged DV mode, no DV banner
            //     popped over a stream that is actually SDR.
            //   * HDR passthrough (LIDSLABS_ALLOW_HDR_TRANSCODE, not force-SDR): a DV
            //     source is passed through as clean HDR10/HLG (no RPU, no -dolby_vision;
            //     the HLS master advertises VIDEO-RANGE=PQ), so flatten the DV
            //     descriptors and the range resolves to HDR10 (smpte2084) / HLG
            //     (arib-std-b67) from ColorTransfer, matching the bytes. A native
            //     HDR10/HLG source already reports correctly and is left untouched.
            //
            // VideoRangeType is a COMPUTED property (MediaStream.GetVideoColorRange)
            // derived from the DV fields + Hdr10PlusPresentFlag + ColorTransfer, so we
            // rewrite those (and, for force-SDR, the color* metadata to bt.709 to mirror
            // the ffmpeg tonemap output). This mutates only the per-request response
            // clone (MediaInfoHelper deep-clones MediaSources before we touch them),
            // never library metadata. Scope: transcode only (TranscodingUrl set). The
            // play decision (SupportsDirectPlay / TranscodingUrl) is finalized above and
            // is NOT changed here, so no client can flip to direct-playing off the
            // rewritten range. Range only; a codec/profile rewrite is a separate follow-up.
            if (lidslabsForceSdrEligible || LidslabsHdrTranscodeEnabled())
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

                    // Read the computed range/range-class BEFORE clearing any fields.
                    var rangeType = videoStream.VideoRangeType;
                    var (videoRange, _) = videoStream.GetVideoColorRange();

                    if (lidslabsForceSdrEligible)
                    {
                        // The transcode tonemaps to SDR. Only HDR/DV sources need the
                        // rewrite; an SDR source already matches the SDR output.
                        if (videoRange != VideoRange.HDR)
                        {
                            continue;
                        }

                        LidslabsClearDoViDescriptors(videoStream);
                        // Flatten HDR color metadata to bt.709 so the range computes SDR
                        // (mirrors the ffmpeg tonemap: bt709 primaries/transfer/matrix).
                        videoStream.ColorPrimaries = "bt709";
                        videoStream.ColorTransfer = "bt709";
                        videoStream.ColorSpace = "bt709";
                        videoStream.Hdr10PlusPresentFlag = null;

                        _logger.LogInformation(
                            "lidslabs: PlaybackInfo delivered-range rewrite (force-SDR: {SourceRange} -> SDR) for profile={ProfileName}, item={ItemId}",
                            rangeType,
                            profile.Name,
                            itemId);
                        continue;
                    }

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
                    // lidslabs 2026-07-04: Moonfin (DeviceProfile.Name "Moonfin", client
                    //     "Moonfin for tvOS", confirmed in the dev capture). Mapped so it
                    //     can take the force-SDR lever. The earlier "intentionally NOT
                    //     mapped" note was based on the 2026-07-02 black screen, which was a
                    //     master.m3u8 HTTP 400 (self-rejected URL, fixed by patch 0012) —
                    //     NOT an HDR problem. With playback working, Moonfin's HDR TRANSCODE
                    //     washes out: its mpv render path cannot engage HDR (the
                    //     outputProvidesHdr()/EDR-headroom gate — an upstream client bug that
                    //     is NOT server-fixable). force-SDR converts that washout into a
                    //     correct SDR image (HEVC preserved). The usual force-SDR side effect
                    //     (it disqualifies HDR/DV DIRECT PLAY) has no path to hit here:
                    //     Moonfin is used only by external, bitrate-limited clients that are
                    //     always transcoding. "Moonfin" is collision-free vs "Trident" /
                    //     "Neptune tvOS" / "1. MPV". See DEBUG_LOG.md 2026-07-04 and
                    //     .project/moonfin-hdr-mpv-washout-issue.md.
                    "moonfin" => profileName.Contains("Moonfin", StringComparison.OrdinalIgnoreCase),
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
        // resolving to a DOVI* range. Shared by both delivered-range rewrite branches
        // (force-SDR -> SDR, and HDR passthrough -> HDR10/HLG). Clears the DV metadata
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
