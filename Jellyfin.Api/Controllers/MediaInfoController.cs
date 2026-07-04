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
        var lidslabsForceHevcEligible =
            profile is not null
            && LidslabsClientMatches(
                profile.Name,
                Environment.GetEnvironmentVariable("LIDSLABS_FORCE_HEVC_CLIENTS"))
            && LidslabsProfileClaimsHevc(profile);

        // lidslabs v0.3: gate-decision diagnostic log. Logs at Debug, so it is
        // silent in a default (Information-level) build and emits nothing in
        // production. Raise the log level to Debug to diagnose a "the override
        // doesn't fire for client X" report in a single cycle — it dumps the
        // gate inputs (profile name, configured client list, HEVC capability)
        // and the eligibility result, distinguishing "patch absent from build"
        // from "a gate failed". (The HDR master toggle is intentionally NOT a
        // gate input here — see the eligibility note above.) See DECISIONS.md,
        // "Diagnostic logging at the eligibility point".
        _logger.LogDebug(
            "lidslabs.forceHevc gate: profile={ProfileName}, clients={Clients}, profileHevc={ProfileHevc}, eligible={Eligible}",
            profile?.Name,
            Environment.GetEnvironmentVariable("LIDSLABS_FORCE_HEVC_CLIENTS"),
            profile is not null && LidslabsProfileClaimsHevc(profile),
            lidslabsForceHevcEligible);

        // lidslabs v0.3.2: Swiftfin (Apple TV) direct-play no-audio fix. Swiftfin's
        // posted DeviceProfile over-claims lossless direct-play — its audio
        // DirectPlayProfiles list truehd with NO restricting audio CodecProfile —
        // but Apple AVPlayer cannot decode TrueHD, so the client direct-plays the
        // stream and renders SILENCE (worse than a transcode fallback). The strip
        // below removes the TrueHD family from the audio direct-play list, forcing
        // audio to transcode: the existing AC3 sidecar redirect then fires when an
        // AC3 track exists, otherwise Jellyfin transcodes the source audio. Either
        // way the user gets audio. (DTS-HD MA is deliberately NOT stripped — the
        // 2026-06-30 capture showed Swiftfin decodes its DTS core and plays fine, so
        // that direct-play claim is not false. See DEBUG_LOG.md 2026-06-30.)
        //
        // Targeting is by AUTHENTICATED CLIENT APP NAME via User.GetClient(), not
        // DeviceProfile.Name: Swiftfin posts Name=null, so the forced-HEVC
        // profile.Name map cannot see it. User.GetClient() == "Jellyfin tvOS" is
        // Swiftfin's stable, unique identity (Swiftfin is the Jellyfin tvOS app).
        // Always-on / code-scoped: correcting a falsely-advertised capability is a
        // fix, not an operator preference (mirrors the always-on AC3 redirect).
        // Single Swiftfin-client gate, shared by the TrueHD audio strip and the
        // DV Profile 7 video strip below (both correct the same client's
        // over-claimed capabilities).
        var lidslabsSwiftfinClient =
            profile is not null
            && string.Equals(User.GetClient(), "Jellyfin tvOS", StringComparison.OrdinalIgnoreCase);

        // Gate-decision diagnostic (Debug; dormant at Information level, like the
        // forced-HEVC gate above). Raise the level to Debug to confirm "is this
        // request seen as Swiftfin?" in one cycle.
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

            // lidslabs v0.3.2: strip the TrueHD family (truehd/mlp) from Swiftfin's
            // audio direct-play profiles so a TrueHD track cannot direct-play to
            // silence. Mutates the shared profile once, pre-loop (kept out of the
            // per-MediaSource loop below — a noted regression risk). Video
            // DirectPlayProfiles only; VideoCodec and Container are untouched, so
            // video still direct-plays/direct-streams and only audio falls to
            // transcode. Inert unless a source actually carries a TrueHD track.
            // (mlp is the TrueHD-family alias used by EncodingHelper's AC3 redirect;
            // it is not in Swiftfin's advertised list today, so stripping it is a
            // harmless no-op there, kept for consistency.)
            if (lidslabsSwiftfinClient)
            {
                var strippedAny = false;
                foreach (var dp in profile.DirectPlayProfiles.Where(p => p.Type == DlnaProfileType.Video))
                {
                    if (string.IsNullOrEmpty(dp.AudioCodec))
                    {
                        continue;
                    }

                    var original = LidslabsSplitTrim(dp.AudioCodec);
                    var kept = original
                        .Where(c => !string.Equals(c, "truehd", StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(c, "mlp", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    if (kept.Length != original.Length)
                    {
                        dp.AudioCodec = string.Join(",", kept);
                        strippedAny = true;
                    }
                }

                if (strippedAny)
                {
                    _logger.LogInformation(
                        "lidslabs: Swiftfin TrueHD audio strip fired for client={Client}, item={ItemId}",
                        User.GetClient(),
                        itemId);
                }
            }

            // lidslabs v0.3.2: Swiftfin over-claims Dolby Vision Profile 7
            // (dual-layer: VideoRangeType DOVIWithEL / DOVIWithELHDR10Plus)
            // direct-play. Its posted profile carries NO VideoRangeType restriction
            // at all (capture 2026-06-30), so Jellyfin hands the Apple TV the raw
            // dual-layer stream — AVPlayer cannot render P7, so HDR never engages,
            // and once audio also leaves direct-play the dual-layer remux corrupts
            // the picture. Inject the VideoRangeType condition Swiftfin should have
            // declared: an EqualsAny allow-list of every range the Apple TV DOES
            // handle (HDR10/HLG/SDR + single-layer DV P5/P8), excluding only the P7
            // EL types. A P7 source then fails this HEVC CodecProfile condition, so
            // StreamBuilder.CanStreamCopyVideo copies the video with the upstream
            // RemoveDovi bitstream filter (dovi_rpu=strip) and delivers the clean
            // HDR10 base — HDR engages, NO re-encode. P5/P8/HDR10 stay direct-play.
            // Added as a dedicated CodecProfile (empty ApplyConditions => always
            // applies to hevc) so Swiftfin's existing entries are left untouched.
            // Idempotent: skipped if a VideoRangeType restriction already exists.
            if (lidslabsSwiftfinClient)
            {
                const string lidslabsAppleTvRangeTypes =
                    "SDR|HDR10|HLG|DOVI|DOVIWithHDR10|DOVIWithHLG|DOVIWithSDR|DOVIWithHDR10Plus|HDR10Plus";

                var alreadyRestricted = profile.CodecProfiles
                    .Where(cp => cp.Type == CodecType.Video
                                 && LidslabsSplitTrim(cp.Codec).Contains("hevc", StringComparer.OrdinalIgnoreCase))
                    .Any(cp => cp.Conditions.Any(c => c.Property == ProfileConditionValue.VideoRangeType));

                if (!alreadyRestricted)
                {
                    var elStrip = new CodecProfile
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
                    };

                    profile.CodecProfiles = profile.CodecProfiles.Append(elStrip).ToArray();

                    _logger.LogInformation(
                        "lidslabs: Swiftfin DV Profile 7 strip applied (injected HEVC VideoRangeType allow-list) for client={Client}, item={ItemId}",
                        User.GetClient(),
                        itemId);
                }
            }

            // lidslabs v0.3.2: force Swiftfin's HLS remux/transcode to fMP4
            // (CMAF) segments instead of MPEG-TS. Apple's AVPlayer — Swiftfin's
            // renderer on Apple TV — cannot decode HEVC inside an MPEG-TS
            // container and renders digital static; on Apple platforms HEVC over
            // HLS must use fMP4. The TrueHD and DV P7 strips above push a
            // lossless/dual-layer title off pure direct-play into a remux, and
            // Jellyfin's default HLS segment container is MPEG-TS, so without
            // this the remux would be HEVC-in-mpegts = static. Rewriting the
            // video HLS TranscodingProfile container ts->mp4 makes StreamInfo
            // emit &SegmentContainer=mp4, which DynamicHlsController muxes as
            // fMP4 — the same effect as Swiftfin's "fMP4 HLS" app setting, but
            // automatic. Container-only: codecs/protocol/conditions are
            // untouched, so the copy-vs-transcode decision is unchanged; only the
            // segment muxing format differs. fMP4 is the Apple-native HLS path,
            // safe for h264/HEVC and SDR/HDR alike. This is the general rule
            // "force fMP4 whenever an Apple-TV AVPlayer client transcodes HEVC"
            // applied to Swiftfin; Neptune AV Player is a documented future
            // adopter (its forced-HEVC "cannot render HLS HEVC" failure is
            // suspected to be this same mpegts bug — see .project docs).
            if (lidslabsSwiftfinClient && LidslabsForceFmp4Hls(profile))
            {
                _logger.LogInformation(
                    "lidslabs: Swiftfin fMP4 HLS force applied (video TranscodingProfile container -> mp4) for client={Client}, item={ItemId}",
                    User.GetClient(),
                    itemId);
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

            // lidslabs v0.3.2 (patch 0013, extended by patch 0014): describe the
            // DELIVERED stream, not the source, for a transcode of a DV/HDR source.
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
            //     tonemapped to SDR (bt.709). Report SDR so the client does NOT engage
            //     DV/HDR over an SDR picture. This is the patch-0014 fix for the 0013 +
            //     force-SDR INTERACTION: 0013 previously self-skipped for force-SDR
            //     clients (gate was !lidslabsForceSdrEligible), leaving the source DV
            //     descriptors in the response -> the TV re-engaged DV mode / popped the
            //     DV banner over a stream that was actually SDR.
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

        // lidslabs v0.3.2 (patch 0013/0014): strip the Dolby Vision descriptors from a
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
