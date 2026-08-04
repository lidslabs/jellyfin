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
using MediaBrowser.Controller.MediaEncoding;
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
    /// <summary>
    /// Default ranked video codec preference (lidslabs v0.4.0).
    /// </summary>
    /// <remarks>
    /// AV1 is deliberately absent. Its advantage is compression efficiency, which converts to
    /// visible quality only where bitrate is scarce, and at the ~20 Mbps these transcodes run at,
    /// HEVC is already at or near transparency. Dolby Vision's per-shot RPU trims are a different
    /// axis that does not diminish with bitrate — so ranking AV1 first would trade a per-scene
    /// accurate picture for compression nobody here needs. Add it for SDR/HDR10-only titles, or
    /// once DV-in-AV1 has renderers worth targeting; the range term in LidslabsCodecPreservesRange
    /// already makes that a pure config change.
    /// </remarks>
    private const string LidslabsDefaultVideoCodecs = "hevc,h264";

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
        //
        // lidslabs v0.3.4: the Swiftfin-specific arm is folded into the shared
        // client-name matcher (LidslabsClientNameMatches), which resolves the SAME
        // friendly-name CSV against the authenticated client string instead of
        // DeviceProfile.Name. "swiftfin" keeps its exact meaning — the tvOS client — and
        // the check is now a proper CSV split rather than a raw substring scan of the env
        // value (the old form would also have matched a hypothetical "not-swiftfin"
        // entry). The iPhone/iPad app gains "jellyfin_ios" as an opt-in on the same lever;
        // it is deliberately NOT in any default, because whether forcing HEVC is right
        // depends on the DEVICE's display, not the app (an iPad 8 has no HDR display at
        // all — see the neptune arm note in LidslabsClientMatches).
        // lidslabs v0.4.0: renamed to LIDSLABS_TRANSCODE_FORCE_HEVC_CLIENTS; the old
        // name is still honoured as an alias so a running deployment does not lose the
        // lever on the next pull. See LidslabsEnv.
        var lidslabsForceHevcClients = LidslabsEnv.Raw(LidslabsEnv.ForceHevcClients);

        var lidslabsForceHevcEligible =
            profile is not null
            && (LidslabsClientMatches(profile.Name, lidslabsForceHevcClients)
                || LidslabsClientNameMatches(User.GetClient(), lidslabsForceHevcClients))
            && LidslabsProfileClaimsHevc(profile);

        // lidslabs v0.3: gate-decision diagnostic log. Logs at Debug, so it is
        // silent in a default (Information-level) build and emits nothing in
        // production. Raise the log level to Debug to diagnose a "the override
        // doesn't fire for client X" report in a single cycle — it dumps the
        // gate inputs (profile name, configured client list, HEVC capability)
        // and the eligibility result. See DECISIONS.md, "Diagnostic logging at
        // the eligibility point".
        // lidslabs v0.3.4: RAISED Debug -> Information, and the client string added to the
        // payload. Rationale (a deliberate, narrow exception to the Debug convention
        // above): every client gate in this file keys off a vendor-supplied string that
        // moves without notice — the tvOS 1.5 rename broke three corrections silently
        // (patch 0017), and the v0.3.4 SDR-ladder bug shipped to prod and survived a full
        // release because a gate that never fired looked identical, in an
        // Information-level log, to a gate that had no work to do. One line per playback
        // start makes the NEGATIVE case visible, which is the case that actually costs us.
        // The other lidslabs gates stay at Debug; only the two client-identity gates are
        // promoted. See DECISIONS.md, "Diagnostic logging at the eligibility point".
        _logger.LogInformation(
            "lidslabs.forceHevc gate: profile={ProfileName}, client={Client}, clients={Clients}, profileHevc={ProfileHevc}, eligible={Eligible}",
            profile?.Name,
            User.GetClient(),
            lidslabsForceHevcClients,
            profile is not null && LidslabsProfileClaimsHevc(profile),
            lidslabsForceHevcEligible);

        // Swiftfin (Apple TV) gate diagnostic — retained as the anchor for the pending
        // per-engine work; see the gate's own comment above.
        _logger.LogDebug(
            "lidslabs.swiftfin gate: client={Client}, eligible={Eligible}",
            User.GetClient(),
            lidslabsSwiftfinClient);

        // lidslabs v0.4.0: report any lever still being read under its pre-v0.4.0 name.
        // Warning, not Debug, and deliberately so — the whole reason the legacy aliases
        // exist is that a silent rename disables levers on the next pull, and this
        // project has now shipped a silently-dead lever twice (the tvOS client rename,
        // patch 0017; the v0.3.4 SDR-ladder allowlist). An alias that works but is never
        // announced just moves the silence one release later. ConsumeLegacyUsages clears
        // its record, so this is one line per legacy name per boot, not per playback.
        var lidslabsLegacyLevers = LidslabsEnv.ConsumeLegacyUsages();
        foreach (var (legacyName, currentName) in lidslabsLegacyLevers)
        {
            _logger.LogWarning(
                "lidslabs: env var {LegacyName} is deprecated and will be removed in a future release; rename it to {CurrentName}",
                legacyName,
                currentName);
        }

        // A lever set under BOTH names with different values is worse than a deprecated one: the
        // new name wins, so the operator's visible setting is shadowed and does nothing. The way
        // that happens in practice is layering -- an image default under the new name against a
        // compose file still using the old one -- which is silent by construction because neither
        // side is wrong on its own. Error rather than Warning: a deprecated name still works, but
        // this one means the deployment is not doing what its configuration says.
        var lidslabsConflicts = LidslabsEnv.ConsumeConflicts();
        foreach (var (legacyName, currentName) in lidslabsConflicts)
        {
            _logger.LogError(
                "lidslabs: env var {LegacyName} is set but IGNORED because {CurrentName} is also set "
                + "with a different value. The value of {CurrentName} is in effect. Remove one.",
                legacyName,
                currentName,
                currentName);
        }

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

            // ================================================================
            // lidslabs v0.4.0: ranked video codec preference
            // ================================================================
            // LIDSLABS_TRANSCODE_PREFERRED_VIDEO_CODEC is a ranked list, newest-first,
            // e.g. "hevc,h264". It reorders each video TranscodingProfile's codec list
            // so StreamBuilder picks the operator's preference rather than the client's
            // own ordering.
            //
            // THREE TERMS, all required:
            //   rank            the operator's order
            //   ∩ advertised    the client must already list the codec. We never invent
            //                   capability — same safeguard as force-HEVC above.
            //   ∩ preserves-range  see below. This is the term that is easy to omit and
            //                   expensive to omit.
            //
            // WHY THE RANGE TERM EXISTS. A naive newest-first list silently destroys the
            // features the rest of this project exists to deliver:
            //   * h264 CANNOT CARRY HDR10 AT ALL. Ranking it above hevc on an HDR source
            //     produces a tonemapped SDR stream with no error anywhere — that was the
            //     Swiftfin washout root cause (DEBUG_LOG 2026-07-21).
            //   * DV RPU preservation is HEVC-ONLY. The RPU is carried in HEVC NAL unit
            //     type 62; DV-in-AV1 is specified but essentially nothing renders it. On a
            //     DV title an "av1"-first list would pick AV1 and DISCARD THE RPU, silently.
            // So the conditional lives HERE, in the selector, not in the env var. The var
            // stays a flat operator preference; adding "av1" later is then a pure config
            // change that cannot accidentally cost us DV or HDR.
            //
            // AV1 is deliberately NOT in the default (Nick, 2026-08-01): its advantage is
            // compression efficiency, which converts to visible quality only when bitrate
            // is scarce, and at ~20 Mbps HEVC is already at or near transparency. DV's
            // per-shot RPU trims are a different axis that does not diminish with bitrate.
            // Add it for SDR/HDR10-only titles, or once DV-in-AV1 has renderers.
            var lidslabsPreferredVideoCodecs =
                LidslabsEnv.List(LidslabsEnv.PreferredVideoCodec, LidslabsDefaultVideoCodecs);

            // Read into a local rather than testing info.MediaSources for null: a
            // `?.` here flips the compiler's null-state for the property and turns the
            // pre-existing `foreach (var mediaSource in info.MediaSources)` further
            // down this method into a CS8602.
            var lidslabsSources = info.MediaSources;
            var lidslabsSourceRange = lidslabsSources
                .SelectMany(ms => ms.MediaStreams ?? Enumerable.Empty<MediaStream>())
                .FirstOrDefault(s => s.Type == MediaStreamType.Video)?
                .VideoRangeType ?? VideoRangeType.Unknown;

            // lidslabs v0.4.0: record what the CLIENT declares it can decode, not just
            // what we decided to send it.
            //
            // We could not answer "do any of our clients actually accept Dolby Vision?"
            // from months of logs, because nothing ever recorded the client side of the
            // negotiation — only our own gate outcomes. That question gates the DV-output
            // feature entirely: emitting a correct P8.1 RPU is worthless if no client
            // asks for DOVIWithHDR10, and we would have shipped it blind.
            //
            // VideoRangeType conditions live in CodecProfiles, not TranscodingProfiles,
            // and a profile that declares NOTHING is not the same as one that declares
            // no DV support — an absent condition means "unconstrained", i.e. the client
            // never narrowed the range at all. Both cases are logged distinctly.
            var lidslabsDeclaredRanges = profile.CodecProfiles
                .SelectMany(cp => cp.Conditions.Concat(cp.ApplyConditions))
                .Where(c => c.Property == ProfileConditionValue.VideoRangeType)
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .SelectMany(v => v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _logger.LogInformation(
                "lidslabs.clientCaps: profile={ProfileName}, client={Client}, sourceRange={SourceRange}, "
                + "declaredRanges={DeclaredRanges}, declaresDovi={DeclaresDovi}, item={ItemId}",
                profile.Name,
                User.GetClient(),
                lidslabsSourceRange,
                lidslabsDeclaredRanges.Length == 0 ? "(none declared)" : string.Join('|', lidslabsDeclaredRanges),
                lidslabsDeclaredRanges.Any(v => v.Contains("DOVI", StringComparison.OrdinalIgnoreCase)),
                itemId);

            if (lidslabsPreferredVideoCodecs.Count > 0)
            {
                var lidslabsRangeEligible = LidslabsCodecPreservesRange(lidslabsSourceRange);
                var lidslabsReordered = false;

                foreach (var tp in profile.TranscodingProfiles.Where(t => t.Type == DlnaProfileType.Video))
                {
                    var advertised = LidslabsSplitTrim(tp.VideoCodec);
                    if (advertised.Length == 0)
                    {
                        continue;
                    }

                    // rank ∩ advertised ∩ preserves-range
                    var ranked = lidslabsPreferredVideoCodecs
                        .Where(c => advertised.Contains(c, StringComparer.OrdinalIgnoreCase))
                        .Where(lidslabsRangeEligible)
                        .ToList();

                    if (ranked.Count == 0)
                    {
                        // Nothing the operator ranked survives for this source. Leave the
                        // client's own list alone rather than narrowing it — a lever that
                        // can only ever REMOVE options is a lever that can break playback.
                        continue;
                    }

                    // Anything advertised but unranked keeps its relative order at the
                    // back, so this is a reordering rather than a whitelist.
                    var remainder = advertised
                        .Where(c => !ranked.Contains(c, StringComparer.OrdinalIgnoreCase));

                    var rewritten = string.Join(",", ranked.Concat(remainder));
                    if (!string.Equals(rewritten, tp.VideoCodec, StringComparison.OrdinalIgnoreCase))
                    {
                        tp.VideoCodec = rewritten;
                        lidslabsReordered = true;
                    }
                }

                // Information, not Debug, and for the same reason the client-identity
                // gates were promoted in v0.3.4: a preference that silently fails to
                // apply looks exactly like one that had no work to do.
                _logger.LogInformation(
                    "lidslabs.videoCodec: profile={ProfileName}, ranked={Ranked}, sourceRange={SourceRange}, reordered={Reordered}",
                    profile.Name,
                    string.Join(",", lidslabsPreferredVideoCodecs),
                    lidslabsSourceRange,
                    lidslabsReordered);
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
            //
            // lidslabs v0.3.4 (patch 0023): PROMOTED TO A SHARED CODE PATH, as the note
            // above instructed once a third adopter appeared. That adopter is the
            // iPhone/iPad app, and for it this is the FIX, not insurance.
            //
            // Why it matters here: the app ships defaulting to its default player AND
            // MPEG-TS, and in that configuration it posts exactly ONE video HLS
            // TranscodingProfile — Container=ts, VideoCodec="h264". It offers hevc only on
            // the mp4 profile it posts when the user turns fMP4 on, which is three separate
            // settings deep. So an out-of-the-box install never asks for HEVC, never reaches
            // the HDR-passthrough path, and never sees the SDR ladder at all: it silently
            // gets H.264 forever on hardware that decodes HEVC in fixed function.
            //
            // Forcing the container alone would NOT fix that — LidslabsForceFmp4Hls touches
            // Container only, so a ts/h264 profile would become mp4/h264 and still deliver
            // H.264 (verified on dev). The codec force is what adds hevc; this transport
            // force is what makes the result playable, because AVPlayer cannot decode
            // HEVC-in-mpegts (patch 0007's finding — it renders static). The two are a
            // matched pair and neither is useful alone, which is why both hang off the same
            // LIDSLABS_FORCE_HEVC_CLIENTS opt-in and no new env var is introduced.
            if (lidslabsForceHevcEligible
                && LidslabsIsApplePlayerClient(profile.Name, User.GetClient())
                && LidslabsForceFmp4Hls(profile))
            {
                _logger.LogInformation(
                    "lidslabs: AVPlayer fMP4 HLS force applied (video TranscodingProfile container -> mp4) for profile={ProfileName}, client={Client}, item={ItemId}",
                    profile.Name,
                    User.GetClient(),
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

            // lidslabs v0.3.3: tag the transcode for the SDR companion rung. The
            // HDR-passthrough master carries an H.264 SDR rung ONLY for Apple AVPlayer
            // clients (they refuse an HDR-only master); every other client already worked
            // without it and some regress with it (Moonfin spins up both rungs -> ~10s
            // black at start). The allowlist is resolved HERE, not at the master.m3u8 GET:
            // this is the only place both the client name (Swiftfin posts DeviceProfile.Name
            // = null, so User.GetClient() is its only handle) AND DeviceProfile.Name (the
            // only field separating Neptune AV from Trident — same client name + UA) are
            // reliable. The master GET is ?ApiKey=-authenticated, where GetClient() does not
            // resolve for AVPlayer clients. We append LidslabsSdrLadder=1 to the
            // TranscodingUrl; DynamicHlsHelper reads that marker back (the client echoes the
            // URL verbatim) and adds the rung. Default set swiftfin,neptune_av,jellyfin_ios;
            // Trident, Streamyfin, Moonfin, Android TV, web are excluded. Tunable via
            // LIDSLABS_SDR_LADDER_CLIENTS.
            //
            // lidslabs v0.3.4: jellyfin_ios ADDED to the default set, and the client-name
            // half of the match generalised (LidslabsClientNameMatches). v0.3.3 shipped the
            // rung with a default of "swiftfin,neptune_av" resolved through
            // LidslabsClientMatches(profile.Name, ...) plus a hardcoded "Swiftfin tvOS"
            // client check — and "swiftfin" has never had a profile.Name arm, so the ONLY
            // way the rung could ever fire was the tvOS client string. Every iOS client
            // therefore fell through to the bare HDR-only master this rung exists to
            // prevent, and prod logs confirm it: zero "SDR-ladder marker added" lines
            // across the whole fleet since v0.3.3 deployed. Symptom on the Jellyfin iPadOS
            // app was total playback failure on any HDR source that reached the HEVC
            // passthrough path, surfacing on its Native player as AVFoundation -11850
            // (AVErrorServerIncorrectlyConfigured — "no variant in this master is playable
            // for me"). DEBUG_LOG 2026-07-30.
            // lidslabs v0.4.0: renamed to LIDSLABS_TRANSCODE_SDR_LADDER_CLIENTS, legacy
            // name still honoured. Prod deliberately leaves this UNSET so it inherits the
            // code default below; dev pins it explicitly.
            // lidslabs v0.4.0: moonfin ADDED to the default set. Measured 2026-08-04 on dev
            // and prod — Moonfin tvOS refuses an HDR-only master (fetches the playlists,
            // requests zero segments, spawns no ffmpeg) and plays as soon as the companion
            // rung is present. It is in the DEFAULT rather than left to the env var on
            // purpose: this is a bug fix, and prod deliberately leaves the var UNSET so it
            // inherits this list. Shipping it behind a lever would mean prod stayed broken
            // until someone remembered to set it. The v0.3.3 exclusion note said Moonfin
            // "spins up both rungs -> ~10s black at start"; that cost is real but it is
            // strictly better than not playing at all, and it is the same trade every other
            // client on this list already makes.
            var sdrLadderClients = LidslabsEnv.Raw(LidslabsEnv.SdrLadderClients);
            if (string.IsNullOrWhiteSpace(sdrLadderClients))
            {
                sdrLadderClients = "swiftfin,neptune_av,jellyfin_ios,moonfin";
            }

            // lidslabs v0.3.4: match on the authenticated CLIENT NAME as well as
            // DeviceProfile.Name. The Jellyfin iOS/iPadOS app is the case that forced this:
            // its two player engines post different DeviceProfile.Names (the default engine
            // posts null, the Native/AVPlayer engine posts an app-supplied name), so no
            // profile-name rule can cover the app as a unit — but both engines authenticate
            // under one stable client string. See LidslabsClientNameMatches.
            var lidslabsSdrLadderEligible =
                LidslabsClientMatches(profile.Name, sdrLadderClients)
                || LidslabsClientNameMatches(User.GetClient(), sdrLadderClients);

            _logger.LogInformation(
                "lidslabs.sdrLadder gate: profile={ProfileName}, client={Client}, clients={Clients}, eligible={Eligible}",
                profile.Name,
                User.GetClient(),
                sdrLadderClients,
                lidslabsSdrLadderEligible);

            if (lidslabsSdrLadderEligible)
            {
                foreach (var mediaSource in info.MediaSources)
                {
                    if (string.IsNullOrEmpty(mediaSource.TranscodingUrl))
                    {
                        continue;
                    }

                    mediaSource.TranscodingUrl += mediaSource.TranscodingUrl.Contains('?', StringComparison.Ordinal)
                        ? "&LidslabsSdrLadder=1"
                        : "?LidslabsSdrLadder=1";

                    _logger.LogInformation(
                        "lidslabs: SDR-ladder marker added to TranscodingUrl for profile={ProfileName}, client={Client}, item={ItemId}",
                        profile.Name,
                        User.GetClient(),
                        itemId);
                }
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

                    // lidslabs v0.4.0: this rewrite is only correct while we emit HDR10.
                    //
                    // The flattening below exists because a transcoded DV source is
                    // delivered as plain HDR10, so reporting DV to the client makes it
                    // select a Dolby Vision display mode for bytes that carry no RPU.
                    // That reasoning inverts the moment we can emit DV ourselves: we
                    // proved this session that ffmpeg + dovi_tool produces a valid
                    // Profile 8.1 stream (dv_bl_signal_compatibility_id=1), and when that
                    // ships, flattening would tell the client "HDR10" while sending DV —
                    // the client's DV path would never engage and the feature would look
                    // like it simply did not work.
                    //
                    // So the condition is named for what actually decides it, rather than
                    // left implicit in "we happen not to emit DV yet". DV output is
                    // deferred (it needs per-segment RPU injection for HLS and a muxer
                    // that writes dvcC — ffmpeg's mp4 muxer does not), so this returns
                    // false today. When the DV path lands it becomes a per-transcode
                    // question asked here, not an archaeology exercise in this method.
                    if (LidslabsTranscodeEmitsDoVi())
                    {
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

        // lidslabs v0.4.0: which output codecs can carry the source's dynamic range.
        //
        // Returns a predicate rather than a list so the caller reads as
        // "rank ∩ advertised ∩ preserves-range" — the third term is the one that is
        // easy to forget, and forgetting it fails SILENTLY: the stream still plays, it
        // is just no longer HDR.
        //
        //   Dolby Vision  -> HEVC only. The RPU rides in HEVC NAL type 62; DV-in-AV1 is
        //                    specified but essentially nothing renders it, so AV1 would
        //                    discard the RPU. h264 cannot carry HDR at all.
        //   HDR10/HLG     -> exclude h264. It has no HDR10 static-metadata carriage, so
        //                    an h264 rung means a tonemapped SDR picture with no error
        //                    logged anywhere (the Swiftfin washout, DEBUG_LOG 2026-07-21).
        //   SDR/unknown   -> unrestricted. Unknown is deliberately permissive: refusing
        //                    to rank on a range we could not compute would disable the
        //                    lever on any item with odd probe output.
        static Func<string, bool> LidslabsCodecPreservesRange(VideoRangeType rangeType)
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
                    // lidslabs v0.3.4: SCOPED TO tvOS. This arm used to be a bare
                    //     Contains("Trident"), which was correct while Trident shipped
                    //     only on Apple TV. Neptune's iOS app posts
                    //     "Neptune iOS (Trident)" and so inherited an Apple-TV-tuned
                    //     lever: on an iPad 8 (no HDR display, see below) the forced
                    //     HEVC transcode came out 3840x2160 HDR10 @ 19.4 Mbps with audio
                    //     copy, which the device renders untone-mapped and then stalls
                    //     on. Requiring "Neptune tvOS" keeps the Apple TV behavior
                    //     byte-identical and drops iOS back to its own negotiated codec.
                    //     iOS can opt in separately via the "neptune_ios" arm below.
                    //     DEBUG_LOG 2026-07-30.
                    "neptune" => profileName.Contains("Neptune tvOS", StringComparison.OrdinalIgnoreCase)
                                 && profileName.Contains("Trident", StringComparison.OrdinalIgnoreCase),
                    // lidslabs v0.3.4: Neptune's iOS/iPadOS app. Trident is its only
                    //     player engine (confirmed 2026-07-30 — the app ships no AV
                    //     Player mode), so one arm covers the app. NOT in any
                    //     default client list: forcing HEVC here is only right for an
                    //     HDR-capable iOS device, which is a per-device call, not a
                    //     per-app one. Opt in by adding "neptune_ios" to
                    //     LIDSLABS_FORCE_HEVC_CLIENTS.
                    "neptune_ios" => profileName.Contains("Neptune iOS", StringComparison.OrdinalIgnoreCase),
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
                    // lidslabs v0.4.0: the "moonfin" arm is BACK, for the SDR COMPANION RUNG
                    //     (not the washout it was originally added for, and not forced HEVC).
                    //     History: added by patch 0014 (v0.3.2) to feed the force-SDR lever,
                    //     removed by patch 0019 (v0.3.3) when that lever was deleted.
                    //
                    //     MEASURED 2026-08-04, from the wire. Moonfin tvOS black-screened on
                    //     every transcode, on dev AND prod. Captured its PlaybackInfo POST and
                    //     the master we serve it: a single variant,
                    //       #EXT-X-STREAM-INF:...,VIDEO-RANGE=PQ,CODECS="hvc1.2.4.L153.B0,..."
                    //     Moonfin fetches master.m3u8 + main.m3u8, posts
                    //     Playing/Progress/Stopped, and requests ZERO segments — no ffmpeg is
                    //     ever spawned. Identical with an Opus manifest and an AC-3 one, so
                    //     audio was never the discriminator. That is the AVPlayer-family
                    //     signature: a client refusing an HDR-ONLY master, which is exactly
                    //     what patch 0020's SDR companion rung exists to prevent. Adding
                    //     moonfin to the SDR-ladder set fixes it — verified on dev.
                    //
                    //     Its DeviceProfile also shows why nothing else fitted: HEVC carries
                    //     NO VideoRangeType condition at all (the DOVI condition is on
                    //     codec="av1"), and DirectPlayProfiles are wide open, which is why
                    //     direct play always worked and only transcodes failed.
                    //
                    //     tvOS-SCOPED, mirroring neptune/neptune_ios: the tvOS profile is
                    //     exactly "Moonfin", the iOS app posts "Moonfin iOS". Opt iOS in via
                    //     the "moonfin_ios" arm if it ever needs the same rung.
                    "moonfin" => profileName.Contains("Moonfin", StringComparison.OrdinalIgnoreCase)
                                 && !profileName.Contains("iOS", StringComparison.OrdinalIgnoreCase),
                    // lidslabs v0.4.0: Moonfin's iOS/iPadOS app. NOT in any default set.
                    "moonfin_ios" => profileName.Contains("Moonfin iOS", StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };

                if (matched)
                {
                    return true;
                }
            }

            return false;
        }

        // lidslabs v0.3.4: the CLIENT-NAME half of the friendly-name matcher.
        //
        // LidslabsClientMatches keys on DeviceProfile.Name, which distinguishes player
        // MODES that ship inside one app (Neptune's Trident vs AV Player post different
        // names under the same client string and UA). That is the right handle there, but
        // it cannot address an app whose engines post different profile names — or none.
        //
        // The Jellyfin iOS/iPadOS app is exactly that case, and is why the v0.3.3 SDR
        // companion rung never fired for it in production: its default player posts
        // DeviceProfile.Name = null while its Native (AVPlayer) player posts an
        // app-supplied name, so neither a null check nor any name substring covers the app
        // as a unit. Both engines do authenticate under one stable client string, which is
        // reliable HERE (PlaybackInfo is bearer-authenticated; the later master.m3u8 GET is
        // ?ApiKey=-authenticated, where User.GetClient() does not resolve — that asymmetry
        // is precisely why the ladder decision is made at PlaybackInfo and carried as a
        // TranscodingUrl marker).
        //
        // Client strings are EXACT matches, not substrings: "Jellyfin iOS" is a strict
        // prefix of nothing today, but the tvOS app's 1.5 rename from "Jellyfin tvOS" to
        // "Swiftfin tvOS" (patch 0017) is the standing proof that these strings move, and a
        // substring rule would silently widen when they do. An unmapped friendly name
        // returns false, same contract as LidslabsClientMatches.
        static bool LidslabsClientNameMatches(string? clientName, string? csv)
        {
            if (string.IsNullOrWhiteSpace(clientName) || string.IsNullOrWhiteSpace(csv))
            {
                return false;
            }

            foreach (var friendly in LidslabsSplitTrim(csv))
            {
                var matched = friendly.ToLowerInvariant() switch
                {
                    // The Apple TV app (App Store name "Swiftfin"). Renamed from
                    //     "Jellyfin tvOS" in its 1.5 release — patch 0017.
                    "swiftfin" => string.Equals(clientName, "Swiftfin tvOS", StringComparison.OrdinalIgnoreCase),
                    // lidslabs v0.3.4: the iPhone/iPad app (App Store name "Jellyfin" —
                    //     there is no "Swiftfin" listing on iOS). It reports
                    //     "Jellyfin " + the OS name, so iPhone and iPad authenticate under
                    //     DIFFERENT strings; both are listed. Confirmed against a live
                    //     server (app 1.7.0): "Jellyfin iPadOS" reported by an iPad,
                    //     "Jellyfin iOS" by an iPhone. DEBUG_LOG 2026-07-30.
                    "jellyfin_ios" => string.Equals(clientName, "Jellyfin iOS", StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(clientName, "Jellyfin iPadOS", StringComparison.OrdinalIgnoreCase),
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

        // lidslabs v0.3.2 (patch 0013): PlaybackInfo needs to know whether an eligible
        // HDR/DV source will be delivered as HDR (passthrough) rather than tonemapped to
        // SDR, so the delivered-range rewrite above never disagrees with what ffmpeg emits.
        //
        // lidslabs v0.4.0: this used to be a hand-maintained MIRROR of the parse in
        // EncodingHelper.IsHdrPassthroughMode — two copies of the same literals that had to
        // be kept in agreement by convention. Both now call the one parse, so they cannot
        // drift. Retained as a named local function purely so the call sites below still
        // read as intent rather than as an env lookup.
        // lidslabs v0.4.0: does THIS transcode deliver a Dolby Vision bitstream?
        //
        // Gates the delivered-range rewrite above. Constant false while the server can
        // only emit HDR10 — see the comment at the call site for why this must become a
        // real per-transcode answer before DV output ships, and what breaks if it does
        // not (client told HDR10, sent DV, never engages its DV path).
        static bool LidslabsTranscodeEmitsDoVi() => false;

        static bool LidslabsHdrTranscodeEnabled()
            => LidslabsEnv.Flag(LidslabsEnv.AllowHdr);

        // Rewrites every video HLS TranscodingProfile's container to mp4 so the
        // resulting HLS stream is muxed as fMP4 (CMAF) instead of MPEG-TS. Apple
        // AVPlayer clients cannot decode HEVC-in-mpegts, so any forced remux for
        // such a client must use fMP4. Returns true if any profile was changed.
        // Idempotent (profiles already on mp4 are left as-is) and scoped to HLS
        // video profiles only — progressive/http and audio profiles are
        // untouched. Reusable: a future Apple-TV AVPlayer client (e.g. Neptune
        // AV Player) can call this from its own gate without re-deriving it.
        // lidslabs v0.3.4 (patch 0023): the single source of truth for "this client renders
        // through Apple AVPlayer", which is the class that cannot decode HEVC (or any
        // non-H.264 codec) inside MPEG-TS and must be served fMP4/CMAF instead.
        //
        // Covers both identity namespaces, because the members are split across them:
        // Neptune's AV Player mode is only separable from Trident by DeviceProfile.Name,
        // while the Apple TV and iPhone/iPad apps are only reliably identified by the
        // authenticated client name. See LidslabsClientMatches / LidslabsClientNameMatches.
        //
        // Deliberately NOT env-driven: this is a statement about a renderer's decoding
        // limits, not a per-deployment preference. It only ever takes effect where a codec
        // force already fired, so it cannot change any client's transport on its own.
        static bool LidslabsIsApplePlayerClient(string? profileName, string? clientName)
            => LidslabsClientMatches(profileName, "neptune_av")
               || LidslabsClientNameMatches(clientName, "swiftfin,jellyfin_ios");

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
