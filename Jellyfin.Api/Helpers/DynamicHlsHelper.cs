using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Dynamic hls helper.
/// </summary>
public class DynamicHlsHelper
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ITranscodeManager _transcodeManager;
    private readonly INetworkManager _networkManager;
    private readonly ILogger<DynamicHlsHelper> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly EncodingHelper _encodingHelper;
    private readonly ITrickplayManager _trickplayManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicHlsHelper"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="transcodeManager">Instance of <see cref="ITranscodeManager"/>.</param>
    /// <param name="networkManager">Instance of the <see cref="INetworkManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{DynamicHlsHelper}"/> interface.</param>
    /// <param name="httpContextAccessor">Instance of the <see cref="IHttpContextAccessor"/> interface.</param>
    /// <param name="encodingHelper">Instance of <see cref="EncodingHelper"/>.</param>
    /// <param name="trickplayManager">Instance of <see cref="ITrickplayManager"/>.</param>
    public DynamicHlsHelper(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IServerConfigurationManager serverConfigurationManager,
        IMediaEncoder mediaEncoder,
        ITranscodeManager transcodeManager,
        INetworkManager networkManager,
        ILogger<DynamicHlsHelper> logger,
        IHttpContextAccessor httpContextAccessor,
        EncodingHelper encodingHelper,
        ITrickplayManager trickplayManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _serverConfigurationManager = serverConfigurationManager;
        _mediaEncoder = mediaEncoder;
        _transcodeManager = transcodeManager;
        _networkManager = networkManager;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _encodingHelper = encodingHelper;
        _trickplayManager = trickplayManager;
    }

    /// <summary>
    /// Get master hls playlist.
    /// </summary>
    /// <param name="transcodingJobType">Transcoding job type.</param>
    /// <param name="streamingRequest">Streaming request dto.</param>
    /// <param name="enableAdaptiveBitrateStreaming">Enable adaptive bitrate streaming.</param>
    /// <returns>A <see cref="Task"/> containing the resulting <see cref="ActionResult"/>.</returns>
    public async Task<ActionResult> GetMasterHlsPlaylist(
        TranscodingJobType transcodingJobType,
        StreamingRequestDto streamingRequest,
        bool enableAdaptiveBitrateStreaming)
    {
        var isHeadRequest = _httpContextAccessor.HttpContext?.Request.Method == WebRequestMethods.Http.Head;
        // CTS lifecycle is managed internally.
        var cancellationTokenSource = new CancellationTokenSource();
        return await GetMasterPlaylistInternal(
            streamingRequest,
            isHeadRequest,
            enableAdaptiveBitrateStreaming,
            transcodingJobType,
            cancellationTokenSource).ConfigureAwait(false);
    }

    private async Task<ActionResult> GetMasterPlaylistInternal(
        StreamingRequestDto streamingRequest,
        bool isHeadRequest,
        bool enableAdaptiveBitrateStreaming,
        TranscodingJobType transcodingJobType,
        CancellationTokenSource cancellationTokenSource)
    {
        if (_httpContextAccessor.HttpContext is null)
        {
            throw new ResourceNotFoundException(nameof(_httpContextAccessor.HttpContext));
        }

        using var state = await StreamingHelpers.GetStreamingState(
                streamingRequest,
                _httpContextAccessor.HttpContext,
                _mediaSourceManager,
                _userManager,
                _libraryManager,
                _serverConfigurationManager,
                _mediaEncoder,
                _encodingHelper,
                _transcodeManager,
                transcodingJobType,
                cancellationTokenSource.Token)
            .ConfigureAwait(false);

        _httpContextAccessor.HttpContext.Response.Headers.Append(HeaderNames.Expires, "0");
        if (isHeadRequest)
        {
            return new FileContentResult(Array.Empty<byte>(), MimeTypes.GetMimeType("playlist.m3u8"));
        }

        var totalBitrate = (state.OutputAudioBitrate ?? 0) + (state.OutputVideoBitrate ?? 0);

        var builder = new StringBuilder();

        builder.AppendLine("#EXTM3U");

        var isLiveStream = state.IsSegmentedLiveStream;

        var queryString = _httpContextAccessor.HttpContext.Request.QueryString.ToString();

        // from universal audio service, need to override the AudioCodec when the actual request differs from original query
        if (!string.Equals(state.OutputAudioCodec, _httpContextAccessor.HttpContext.Request.Query["AudioCodec"].ToString(), StringComparison.OrdinalIgnoreCase))
        {
            var newQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);
            newQuery["AudioCodec"] = state.OutputAudioCodec;
            queryString = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(string.Empty, newQuery);
        }

        // from universal audio service
        if (!string.IsNullOrWhiteSpace(state.Request.SegmentContainer)
            && !queryString.Contains("SegmentContainer", StringComparison.OrdinalIgnoreCase))
        {
            queryString += "&SegmentContainer=" + state.Request.SegmentContainer;
        }

        // from universal audio service
        if (!string.IsNullOrWhiteSpace(state.Request.TranscodeReasons)
            && !queryString.Contains("TranscodeReasons=", StringComparison.OrdinalIgnoreCase))
        {
            queryString += "&TranscodeReasons=" + state.Request.TranscodeReasons;
        }

        // Video rotation metadata is only supported in fMP4 remuxing
        if (state.VideoStream is not null
            && state.VideoRequest is not null
            && (state.VideoStream?.Rotation ?? 0) != 0
            && EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
            && !string.IsNullOrWhiteSpace(state.Request.SegmentContainer)
            && !string.Equals(state.Request.SegmentContainer, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            queryString += "&AllowVideoStreamCopy=false";
        }

        // Main stream
        var baseUrl = isLiveStream ? "live.m3u8" : "main.m3u8";
        var playlistUrl = baseUrl + queryString;
        var playlistQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);

        var subtitleStreams = state.MediaSource
            .MediaStreams
            .Where(i => i.IsTextSubtitleStream)
            .ToList();

        var subtitleGroup = subtitleStreams.Count > 0 && (state.SubtitleDeliveryMethod == SubtitleDeliveryMethod.Hls || state.VideoRequest!.EnableSubtitlesInManifest)
            ? "subs"
            : null;

        // If we're burning in subtitles then don't add additional subs to the manifest
        if (state.SubtitleStream is not null && state.SubtitleDeliveryMethod == SubtitleDeliveryMethod.Encode)
        {
            subtitleGroup = null;
        }

        if (!string.IsNullOrWhiteSpace(subtitleGroup))
        {
            AddSubtitles(state, subtitleStreams, builder, _httpContextAccessor.HttpContext.User);
        }

        var basicPlaylist = AppendPlaylist(builder, state, playlistUrl, totalBitrate, subtitleGroup);

        if (state.VideoStream is not null && state.VideoRequest is not null)
        {
            var encodingOptions = _serverConfigurationManager.GetEncodingOptions();

            // lidslabs v0.4.0: negotiate the COPY path's SDR rung codec, exactly as v0.3.4 already
            // does for the passthrough-transcode rung below.
            //
            // v0.3.4 taught this file that "no HDR display" must not imply "no HEVC" -- but it only
            // taught the transcode rung. A remux (container/audio only, video copied) never reaches
            // that rung: it is gated on !IsCopyCodec. It lands here instead, in stock Jellyfin's
            // copy-path ladder, which emits an HEVC SDR entrance AND an H.264 SDR entrance at the
            // SAME BANDWIDTH -- and HLS defines no tie-break between two variants that differ only
            // in CODECS. Measured on dev 2026-08-03: Neptune AV, offered exactly that pair, took
            // H.264, on a client we explicitly force HEVC for. That is the ~40% efficiency loss
            // v0.3.4 exists to prevent, still live on every HDR title that remuxes.
            //
            // So apply the same rule here: SWAP the rung's codec, never ADD a second rung. The
            // decision is the same helper, on the query the client echoed back, evaluated BEFORE
            // anything mutates it -- see the defensive copies below for why that ordering is
            // load-bearing.
            //
            // AV1 is deliberately left alone. It is gated on the source already being AV1, so it
            // can never co-occur with the HEVC rung, and narrowing it would risk leaving an
            // AV1-incapable client with no SDR entrance at all. Its behaviour is byte-identical.
            var lidslabsSdrRungCodec = LidslabsSdrRungCodec(playlistQuery);
            var lidslabsHevcSdrRungEmitted = false;

            // Provide AV1 and HEVC SDR entrances for backward compatibility.
            foreach (var sdrVideoCodec in new[] { "av1", "hevc" })
            {
                var isAv1EncodingAllowed = encodingOptions.AllowAv1Encoding
                    && string.Equals(sdrVideoCodec, "av1", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(state.ActualOutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase);
                var isHevcEncodingAllowed = encodingOptions.AllowHevcEncoding
                    && string.Equals(sdrVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(state.ActualOutputVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)
                    // lidslabs: only when the client actually asked for HEVC and can take it as SDR.
                    && string.Equals(lidslabsSdrRungCodec, "hevc", StringComparison.OrdinalIgnoreCase);
                var isEncodingAllowed = isAv1EncodingAllowed || isHevcEncodingAllowed;

                if (isEncodingAllowed
                    && EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
                    && state.VideoStream.VideoRange == VideoRange.HDR)
                {
                    // Force AV1 and HEVC Main Profile and disable video stream copy.
                    state.OutputVideoCodec = sdrVideoCodec;

                    // lidslabs: a COPY, not the alias stock used. `var x = playlistQuery` handed
                    // every block the caller's own dictionary, so each rung's writes persisted into
                    // the next one and into everything downstream -- visible on the wire as an
                    // `hevc-profile=main` we wrote for the HEVC rung riding along on the H.264 URL.
                    // It also corrupts the input this fix reads: by the H.264 block below,
                    // playlistQuery["VideoCodec"] was already "hevc" -- a value we wrote ourselves.
                    var sdrPlaylistQuery = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(playlistQuery);
                    sdrPlaylistQuery["VideoCodec"] = sdrVideoCodec;
                    sdrPlaylistQuery[sdrVideoCodec + "-profile"] = "main";
                    sdrPlaylistQuery["AllowVideoStreamCopy"] = "false";

                    var sdrVideoUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(baseUrl, sdrPlaylistQuery);

                    // HACK: Use the same bitrate so that the client can choose by other attributes, such as color range.
                    AppendPlaylist(builder, state, sdrVideoUrl, totalBitrate, subtitleGroup);

                    // Restore the video codec
                    state.OutputVideoCodec = "copy";

                    if (isHevcEncodingAllowed)
                    {
                        lidslabsHevcSdrRungEmitted = true;
                    }
                }
            }

            // Provide H.264 SDR entrance for backward compatibility.
            // lidslabs: skipped when the HEVC rung above already provided one. Emitting both would
            // put two SDR variants at the same BANDWIDTH in the master and leave the client's choice
            // between them undefined -- the exact hazard the passthrough rung's "swap, never an
            // addition" rule was written for.
            if (!lidslabsHevcSdrRungEmitted
                && EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
                && state.VideoStream.VideoRange == VideoRange.HDR)
            {
                // Force H.264 and disable video stream copy.
                state.OutputVideoCodec = "h264";

                // lidslabs: copy, not alias -- see the HEVC rung above.
                var sdrPlaylistQuery = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(playlistQuery);
                sdrPlaylistQuery["VideoCodec"] = "h264";
                sdrPlaylistQuery["AllowVideoStreamCopy"] = "false";

                var sdrVideoUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(baseUrl, sdrPlaylistQuery);

                // HACK: Use the same bitrate so that the client can choose by other attributes, such as color range.
                AppendPlaylist(builder, state, sdrVideoUrl, totalBitrate, subtitleGroup);

                // Restore the video codec
                state.OutputVideoCodec = "copy";
            }

            // lidslabs v0.3.3: the HDR *passthrough transcode* path emits a single
            // VIDEO-RANGE=PQ variant with no SDR companion. Apple AVPlayer clients (Swiftfin,
            // Neptune AV) silently refuse to start such an HDR-only master -- they fetch the
            // media playlist then never request a segment (stall with no error). Stock
            // Jellyfin already provides an SDR entrance for HDR content, but only on the copy
            // path (the IsCopyCodec guards above); our passthrough is a transcode
            // (OutputVideoCodec = hevc), so it never got one. Add an H.264 (tonemapped SDR)
            // companion so the master advertises an HDR+SDR ladder; on an HDR-capable display
            // AVPlayer commits to the master and (for capable builds) selects the PQ variant
            // (verified: Swiftfin plays HDR from start/seek/resume; Neptune AV plays SDR).
            //
            // SCOPE (LidslabsSdrLadderRequested): fire ONLY when PlaybackInfo tagged this
            // stream for the known-AVPlayer allowlist. Every other client already worked
            // without the rung and pays a real cost if it gets one -- e.g. Moonfin eagerly
            // spins up BOTH rungs at start, so two 1440p NVENC transcodes contend on CUDA init
            // + source probe and starve the opening segments (~10 s black before it
            // self-heals). Clients that play HDR natively (Trident), via mpv (Streamyfin), or
            // that we have not characterised (Android TV, web) therefore get the clean
            // HDR-only master. The decision is made at PlaybackInfo (MediaInfoController),
            // where the client name AND DeviceProfile.Name are both reliable, and carried here
            // as a TranscodingUrl marker; the master.m3u8 GET's ?ApiKey= auth makes
            // User.GetClient() unreliable at this point, so we must not re-derive it here.
            if (EncodingHelper.IsHdrPassthroughMode(state)
                && !EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
                && LidslabsSdrLadderRequested())
            {
                // lidslabs v0.3.4: the SDR rung's CODEC is negotiated, not hardcoded.
                //
                // v0.3.3 always emitted H.264 here, copied from the stock SDR rung above
                // (whose job is a maximum-compatibility entrance on the copy path). That was
                // the wrong call for this rung: its audience is Apple AVPlayer clients, which
                // decode HEVC natively. The result was that a device with no HDR display —
                // e.g. an iPad 8, sRGB panel, no P3 — correctly self-selected the SDR rung and
                // was thereby pinned to H.264 forever, losing ~40% efficiency on exactly the
                // constrained links where it matters most. "No HDR display" must not imply
                // "no HEVC".
                //
                // So: emit an HEVC SDR rung when the client's OWN echoed query says it asked
                // for HEVC, else keep H.264 byte-identically. This is a swap, never an
                // addition — two rungs total (PQ + SDR), because a third rung at the same
                // BANDWIDTH would make the client's choice between two SDR variants
                // undefined (see the same-bitrate HACK below).
                //
                // Everything needed is already in playlistQuery, which the client echoed back
                // to us — no DeviceProfile access needed at this point (and none is reliable
                // here; see LidslabsSdrLadderRequested).
                var originalVideoCodec = state.OutputVideoCodec;
                var originalRangeType = state.BaseRequest.VideoRangeType;
                var originalProfile = state.BaseRequest.Profile;

                var sdrCodec = LidslabsSdrRungCodec(playlistQuery);

                var sdrPlaylistQuery = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(playlistQuery);
                sdrPlaylistQuery["VideoCodec"] = sdrCodec;
                sdrPlaylistQuery["AllowVideoStreamCopy"] = "false";

                state.OutputVideoCodec = sdrCodec;

                if (string.Equals(sdrCodec, "hevc", StringComparison.OrdinalIgnoreCase))
                {
                    // Ask for SDR explicitly. IsHdrPassthroughMode honours an all-SDR
                    // requested range and drops to the stock tonemap path, so this single
                    // switch flips BOTH the manifest (VIDEO-RANGE) and the ffmpeg filter
                    // chain together — the invariant that gate was written to guarantee.
                    // Without it, target codec "hevc" + an HDR source would re-enter
                    // passthrough and this rung would be a duplicate of the PQ one.
                    sdrPlaylistQuery["hevc-rangetype"] = "SDR";
                    state.BaseRequest.VideoRangeType = "SDR";

                    // Pin BOTH sides to Main. The tonemap emits yuv420p (8-bit), so the
                    // bitstream is HEVC Main — but the client's own request carries
                    // hevc-profile=main10 (from its "main|main 10" profile condition), which
                    // GetOutputVideoCodecProfile would echo into CODECS as hvc1.2.4 (Main10).
                    // Verified on device: declared Main10 while ffmpeg reported
                    // "hevc (Main) ... bt709". AVPlayer refuses a CODECS/bitstream mismatch —
                    // the same failure mode patch 0016 pinned tier/level for — so an
                    // unpinned HEVC rung would have silently re-created the very -11850 this
                    // ladder exists to prevent.
                    sdrPlaylistQuery["hevc-profile"] = "main";
                    state.BaseRequest.Profile = "main";
                }

                var sdrVideoUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(baseUrl, sdrPlaylistQuery);

                // HACK: Use the same bitrate so that the client can choose by other attributes, such as color range.
                AppendPlaylist(builder, state, sdrVideoUrl, totalBitrate, subtitleGroup);

                // Restore the real (transcode) output codec and the request's range/profile.
                state.OutputVideoCodec = originalVideoCodec;
                state.BaseRequest.VideoRangeType = originalRangeType;
                state.BaseRequest.Profile = originalProfile;
            }

            // Provide Level 5.0 entrance for backward compatibility.
            // e.g. Apple A10 chips refuse the master playlist containing SDR HEVC Main Level 5.1 video,
            // but in fact it is capable of playing videos up to Level 6.1.
            if (EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
                && state.VideoStream.Level.HasValue
                && state.VideoStream.Level > 150
                && state.VideoStream.VideoRange == VideoRange.SDR
                && string.Equals(state.ActualOutputVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
            {
                var playlistCodecsField = new StringBuilder();
                AppendPlaylistCodecsField(playlistCodecsField, state);

                // Force the video level to 5.0.
                var originalLevel = state.VideoStream.Level;
                state.VideoStream.Level = 150;
                var newPlaylistCodecsField = new StringBuilder();
                AppendPlaylistCodecsField(newPlaylistCodecsField, state);

                // Restore the video level.
                state.VideoStream.Level = originalLevel;
                var newPlaylist = ReplacePlaylistCodecsField(basicPlaylist, playlistCodecsField, newPlaylistCodecsField);
                builder.Append(newPlaylist);
            }
        }

        if (EnableAdaptiveBitrateStreaming(state, isLiveStream, enableAdaptiveBitrateStreaming, _httpContextAccessor.HttpContext.GetNormalizedRemoteIP()))
        {
            var requestedVideoBitrate = state.VideoRequest?.VideoBitRate ?? 0;

            // By default, vary by just 200k
            var variation = GetBitrateVariation(totalBitrate);

            var newBitrate = totalBitrate - variation;
            // lidslabs: copy, not alias. Stock aliased the caller's dictionary here too, so the
            // adaptive-bitrate variants inherited whatever the SDR rungs above had written into it
            // -- an ABR ladder built from the SDR rung's codec rather than the variant it is meant
            // to shadow. The rungs no longer mutate it, so this is defence in depth.
            var variantQuery = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(playlistQuery);
            variantQuery["VideoBitrate"] = (requestedVideoBitrate - variation).ToString(CultureInfo.InvariantCulture);
            var variantUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(baseUrl, variantQuery);
            AppendPlaylist(builder, state, variantUrl, newBitrate, subtitleGroup);

            variation *= 2;
            newBitrate = totalBitrate - variation;
            variantQuery["VideoBitrate"] = (requestedVideoBitrate - variation).ToString(CultureInfo.InvariantCulture);
            variantUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(baseUrl, variantQuery);
            AppendPlaylist(builder, state, variantUrl, newBitrate, subtitleGroup);
        }

        if (!isLiveStream && (state.VideoRequest?.EnableTrickplay ?? false))
        {
            var sourceId = Guid.Parse(state.Request.MediaSourceId);
            var trickplayResolutions = await _trickplayManager.GetTrickplayResolutions(sourceId).ConfigureAwait(false);
            AddTrickplay(state, trickplayResolutions, builder, _httpContextAccessor.HttpContext.User);
        }

        return new FileContentResult(Encoding.UTF8.GetBytes(builder.ToString()), MimeTypes.GetMimeType("playlist.m3u8"));
    }

    /// <summary>
    /// lidslabs v0.3.3: whether the SDR companion rung was requested for this stream.
    /// The rung exists solely to make Apple AVPlayer clients commit to an HDR master; every
    /// other client already worked without it and some pay a real cost for it (see the call
    /// site). The AVPlayer-client decision is made upstream at PlaybackInfo — the only place
    /// where both the authenticated client name AND DeviceProfile.Name are reliable — which
    /// tags the TranscodingUrl with LidslabsSdrLadder=1. We must NOT re-derive it here: the
    /// master.m3u8 GET is authenticated by an ?ApiKey= query for AVPlayer clients, so
    /// User.GetClient() does not reliably resolve the client at this point (it returned
    /// nothing for Swiftfin while resolving for Neptune — the exact split that regressed).
    /// Reading the marker the client echoes back is auth-independent and precise (Trident,
    /// separable from Neptune AV only by DeviceProfile.Name, is correctly excluded upstream).
    /// </summary>
    private bool LidslabsSdrLadderRequested()
        => string.Equals(
            _httpContextAccessor.HttpContext?.Request.Query["LidslabsSdrLadder"].ToString(),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// lidslabs v0.3.4: which video codec the SDR companion rung should carry.
    /// <para>
    /// Returns "hevc" when the client asked for HEVC and can take it as SDR, else "h264".
    /// The decision is made purely from the playlist query the client echoed back, because
    /// that IS the client's own negotiated request — StreamBuilder already reduced its
    /// DeviceProfile to these parameters at PlaybackInfo, and the DeviceProfile itself is
    /// not reachable here (see LidslabsSdrLadderRequested for why client identity is
    /// unreliable at this point).
    /// </para>
    /// <para>
    /// Three conditions, all of which must hold. (1) VideoCodec lists hevc — the client
    /// asked for it. (2) The hevc range list includes SDR, or is absent entirely; a client
    /// that explicitly restricted hevc to HDR-only ranges must not be handed HEVC SDR.
    /// (3) The segment container is fMP4. This one is not optional: Apple AVPlayer cannot
    /// decode HEVC in MPEG-TS (it renders static — the finding behind patch 0007's fMP4
    /// force), and this rung's entire audience is AVPlayer clients. A ts-segmented ladder
    /// therefore keeps H.264.
    /// </para>
    /// </summary>
    /// <param name="playlistQuery">The query the client echoed back on the master request.</param>
    /// <returns>"hevc" or "h264".</returns>
    private static string LidslabsSdrRungCodec(
        IDictionary<string, Microsoft.Extensions.Primitives.StringValues> playlistQuery)
    {
        const string Fallback = "h264";

        static string[] SplitCsv(Microsoft.Extensions.Primitives.StringValues value)
            => value.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!playlistQuery.TryGetValue("VideoCodec", out var requestedCodecs)
            || !SplitCsv(requestedCodecs).Contains("hevc", StringComparer.OrdinalIgnoreCase))
        {
            return Fallback;
        }

        // An explicit hevc range list that excludes SDR means "HEVC only for HDR".
        // Absent = unconstrained = SDR is acceptable.
        if (playlistQuery.TryGetValue("hevc-rangetype", out var hevcRangeTypes))
        {
            var ranges = SplitCsv(hevcRangeTypes);
            if (ranges.Length > 0 && !ranges.Contains("SDR", StringComparer.OrdinalIgnoreCase))
            {
                return Fallback;
            }
        }

        if (!playlistQuery.TryGetValue("SegmentContainer", out var segmentContainer)
            || !string.Equals(segmentContainer.ToString(), "mp4", StringComparison.OrdinalIgnoreCase))
        {
            return Fallback;
        }

        return "hevc";
    }

    private StringBuilder AppendPlaylist(StringBuilder builder, StreamState state, string url, int bitrate, string? subtitleGroup)
    {
        var playlistBuilder = new StringBuilder();
        playlistBuilder.Append("#EXT-X-STREAM-INF:BANDWIDTH=")
            .Append(bitrate.ToString(CultureInfo.InvariantCulture))
            .Append(",AVERAGE-BANDWIDTH=")
            .Append(bitrate.ToString(CultureInfo.InvariantCulture));

        AppendPlaylistVideoRangeField(playlistBuilder, state);

        AppendPlaylistCodecsField(playlistBuilder, state);

        AppendPlaylistSupplementalCodecsField(playlistBuilder, state);

        AppendPlaylistResolutionField(playlistBuilder, state);

        AppendPlaylistFramerateField(playlistBuilder, state);

        if (!string.IsNullOrWhiteSpace(subtitleGroup))
        {
            playlistBuilder.Append(",SUBTITLES=\"")
                .Append(subtitleGroup)
                .Append('"');
        }

        playlistBuilder.Append(Environment.NewLine);
        playlistBuilder.AppendLine(url);
        builder.Append(playlistBuilder);

        return playlistBuilder;
    }

    /// <summary>
    /// Appends a VIDEO-RANGE field containing the range of the output video stream.
    /// </summary>
    /// <seealso cref="AppendPlaylist(StringBuilder, StreamState, string, int, string)"/>
    /// <param name="builder">StringBuilder to append the field to.</param>
    /// <param name="state">StreamState of the current stream.</param>
    private void AppendPlaylistVideoRangeField(StringBuilder builder, StreamState state)
    {
        if (state.VideoStream is not null && state.VideoStream.VideoRange != VideoRange.Unknown)
        {
            var videoRange = state.VideoStream.VideoRange;
            var videoRangeType = state.VideoStream.VideoRangeType;

            // Emit HDR signaling when:
            //  (a) the stream is being copied (existing behavior), OR
            //  (b) the transcode pipeline is in HDR passthrough mode, in which case
            //      the output bytes are HDR even though OutputVideoCodec != "copy".
            // Without case (b), the manifest declares VIDEO-RANGE=SDR while the
            // bitstream carries BT.2020/PQ, which causes some players (ExoPlayer in
            // particular) to mis-initialize the rendering pipeline.
            var isHdrOutput = EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
                || EncodingHelper.IsHdrPassthroughMode(state);

            if (isHdrOutput)
            {
                if (videoRange == VideoRange.SDR)
                {
                    builder.Append(",VIDEO-RANGE=SDR");
                }

                if (videoRange == VideoRange.HDR)
                {
                    switch (videoRangeType)
                    {
                        case VideoRangeType.HLG:
                        case VideoRangeType.DOVIWithHLG:
                            builder.Append(",VIDEO-RANGE=HLG");
                            break;
                        default:
                            builder.Append(",VIDEO-RANGE=PQ");
                            break;
                    }
                }
            }
            else
            {
                // Encoding to SDR (the tonemap path)
                builder.Append(",VIDEO-RANGE=SDR");
            }
        }
    }

    /// <summary>
    /// Appends a CODECS field containing formatted strings of
    /// the active streams output video and audio codecs.
    /// </summary>
    /// <seealso cref="AppendPlaylist(StringBuilder, StreamState, string, int, string)"/>
    /// <seealso cref="GetPlaylistVideoCodecs(StreamState, string, int)"/>
    /// <seealso cref="GetPlaylistAudioCodecs(StreamState)"/>
    /// <param name="builder">StringBuilder to append the field to.</param>
    /// <param name="state">StreamState of the current stream.</param>
    private void AppendPlaylistCodecsField(StringBuilder builder, StreamState state)
    {
        // Video
        string videoCodecs = string.Empty;
        int? videoCodecLevel = GetOutputVideoCodecLevel(state);
        if (!string.IsNullOrEmpty(state.ActualOutputVideoCodec) && videoCodecLevel.HasValue)
        {
            videoCodecs = GetPlaylistVideoCodecs(state, state.ActualOutputVideoCodec, videoCodecLevel.Value);
        }

        // Audio
        string audioCodecs = string.Empty;
        if (!string.IsNullOrEmpty(state.ActualOutputAudioCodec))
        {
            audioCodecs = GetPlaylistAudioCodecs(state);
        }

        StringBuilder codecs = new StringBuilder();

        codecs.Append(videoCodecs);

        if (!string.IsNullOrEmpty(videoCodecs) && !string.IsNullOrEmpty(audioCodecs))
        {
            codecs.Append(',');
        }

        codecs.Append(audioCodecs);

        if (codecs.Length > 1)
        {
            builder.Append(",CODECS=\"")
                .Append(codecs)
                .Append('"');
        }
    }

    /// <summary>
    /// Appends a SUPPLEMENTAL-CODECS field containing formatted strings of
    /// the active streams output Dolby Vision Videos.
    /// </summary>
    /// <seealso cref="AppendPlaylist(StringBuilder, StreamState, string, int, string)"/>
    /// <seealso cref="GetPlaylistVideoCodecs(StreamState, string, int)"/>
    /// <param name="builder">StringBuilder to append the field to.</param>
    /// <param name="state">StreamState of the current stream.</param>
    private void AppendPlaylistSupplementalCodecsField(StringBuilder builder, StreamState state)
    {
        // HDR dynamic metadata currently cannot exist when transcoding
        if (!EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
        {
            return;
        }

        if (EncodingHelper.IsDovi(state.VideoStream) && !_encodingHelper.IsDoviRemoved(state))
        {
            AppendDvString();
        }
        else if (EncodingHelper.IsHdr10Plus(state.VideoStream) && !_encodingHelper.IsHdr10PlusRemoved(state))
        {
            AppendHdr10PlusString();
        }

        return;

        void AppendDvString()
        {
            var dvProfile = state.VideoStream.DvProfile;
            var dvLevel = state.VideoStream.DvLevel;
            var dvRangeString = state.VideoStream.VideoRangeType switch
            {
                VideoRangeType.DOVIWithHDR10 => "db1p",
                VideoRangeType.DOVIWithHLG => "db4h",
                VideoRangeType.DOVIWithHDR10Plus => "db1p", // The HDR10+ metadata would be removed if Dovi metadata is not removed
                _ => string.Empty // Don't label Dovi with EL and SDR due to compatability issues, ignore invalid configurations
            };

            if (dvProfile is null || dvLevel is null || string.IsNullOrEmpty(dvRangeString))
            {
                return;
            }

            var dvFourCc = string.Equals(state.ActualOutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase) ? "dav1" : "dvh1";
            builder.Append(",SUPPLEMENTAL-CODECS=\"")
                .Append(dvFourCc)
                .Append('.')
                .Append(dvProfile.Value.ToString("D2", CultureInfo.InvariantCulture))
                .Append('.')
                .Append(dvLevel.Value.ToString("D2", CultureInfo.InvariantCulture))
                .Append('/')
                .Append(dvRangeString)
                .Append('"');
        }

        void AppendHdr10PlusString()
        {
            var videoCodecLevel = GetOutputVideoCodecLevel(state);
            if (string.IsNullOrEmpty(state.ActualOutputVideoCodec) || videoCodecLevel is null)
            {
                return;
            }

            var videoCodecString = GetPlaylistVideoCodecs(state, state.ActualOutputVideoCodec, videoCodecLevel.Value);
            builder.Append(",SUPPLEMENTAL-CODECS=\"")
                .Append(videoCodecString)
                .Append('/')
                .Append("cdm4")
                .Append('"');
        }
    }

    /// <summary>
    /// Appends a RESOLUTION field containing the resolution of the output stream.
    /// </summary>
    /// <seealso cref="AppendPlaylist(StringBuilder, StreamState, string, int, string)"/>
    /// <param name="builder">StringBuilder to append the field to.</param>
    /// <param name="state">StreamState of the current stream.</param>
    private void AppendPlaylistResolutionField(StringBuilder builder, StreamState state)
    {
        if (state.OutputWidth.HasValue && state.OutputHeight.HasValue)
        {
            builder.Append(",RESOLUTION=")
                .Append(state.OutputWidth.GetValueOrDefault())
                .Append('x')
                .Append(state.OutputHeight.GetValueOrDefault());
        }
    }

    /// <summary>
    /// Appends a FRAME-RATE field containing the framerate of the output stream.
    /// </summary>
    /// <seealso cref="AppendPlaylist(StringBuilder, StreamState, string, int, string)"/>
    /// <param name="builder">StringBuilder to append the field to.</param>
    /// <param name="state">StreamState of the current stream.</param>
    private void AppendPlaylistFramerateField(StringBuilder builder, StreamState state)
    {
        double? framerate = null;
        if (state.TargetFramerate.HasValue)
        {
            framerate = Math.Round(state.TargetFramerate.GetValueOrDefault(), 3);
        }
        else if (state.VideoStream?.RealFrameRate is not null)
        {
            framerate = Math.Round(state.VideoStream.RealFrameRate.GetValueOrDefault(), 3);
        }

        if (framerate.HasValue)
        {
            builder.Append(",FRAME-RATE=")
                .Append(framerate.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private bool EnableAdaptiveBitrateStreaming(StreamState state, bool isLiveStream, bool enableAdaptiveBitrateStreaming, IPAddress ipAddress)
    {
        // Within the local network this will likely do more harm than good.
        if (_networkManager.IsInLocalNetwork(ipAddress))
        {
            return false;
        }

        if (!enableAdaptiveBitrateStreaming)
        {
            return false;
        }

        if (isLiveStream || string.IsNullOrWhiteSpace(state.MediaPath))
        {
            // Opening live streams is so slow it's not even worth it
            return false;
        }

        if (EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
        {
            return false;
        }

        if (EncodingHelper.IsCopyCodec(state.OutputAudioCodec))
        {
            return false;
        }

        if (!state.IsOutputVideo)
        {
            return false;
        }

        return state.VideoRequest?.VideoBitRate.HasValue ?? false;
    }

    private void AddSubtitles(StreamState state, IEnumerable<MediaStream> subtitles, StringBuilder builder, ClaimsPrincipal user)
    {
        if (state.SubtitleDeliveryMethod == SubtitleDeliveryMethod.Drop)
        {
            return;
        }

        var selectedIndex = state.SubtitleStream is null || state.SubtitleDeliveryMethod != SubtitleDeliveryMethod.Hls ? (int?)null : state.SubtitleStream.Index;
        const string Format = "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"{0}\",DEFAULT={1},FORCED={2},AUTOSELECT=YES,URI=\"{3}\",LANGUAGE=\"{4}\"";

        foreach (var stream in subtitles)
        {
            var name = stream.DisplayTitle;

            var isDefault = selectedIndex.HasValue && selectedIndex.Value == stream.Index;
            var isForced = stream.IsForced;

            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/Subtitles/{1}/subtitles.m3u8?SegmentLength={2}&ApiKey={3}",
                state.Request.MediaSourceId,
                stream.Index.ToString(CultureInfo.InvariantCulture),
                30.ToString(CultureInfo.InvariantCulture),
                user.GetToken());

            var line = string.Format(
                CultureInfo.InvariantCulture,
                Format,
                name,
                isDefault ? "YES" : "NO",
                isForced ? "YES" : "NO",
                url,
                stream.Language ?? "Unknown");

            builder.AppendLine(line);
        }
    }

    /// <summary>
    /// Appends EXT-X-IMAGE-STREAM-INF playlists for each available trickplay resolution.
    /// </summary>
    /// <param name="state">StreamState of the current stream.</param>
    /// <param name="trickplayResolutions">Dictionary of widths to corresponding tiles info.</param>
    /// <param name="builder">StringBuilder to append the field to.</param>
    /// <param name="user">Http user context.</param>
    private void AddTrickplay(StreamState state, Dictionary<int, TrickplayInfo> trickplayResolutions, StringBuilder builder, ClaimsPrincipal user)
    {
        const string playlistFormat = "#EXT-X-IMAGE-STREAM-INF:BANDWIDTH={0},RESOLUTION={1}x{2},CODECS=\"jpeg\",URI=\"{3}\"";

        foreach (var resolution in trickplayResolutions)
        {
            var width = resolution.Key;
            var trickplayInfo = resolution.Value;

            var url = string.Format(
                CultureInfo.InvariantCulture,
                "Trickplay/{0}/tiles.m3u8?MediaSourceId={1}&ApiKey={2}",
                width.ToString(CultureInfo.InvariantCulture),
                state.Request.MediaSourceId,
                user.GetToken());

            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                playlistFormat,
                trickplayInfo.Bandwidth.ToString(CultureInfo.InvariantCulture),
                trickplayInfo.Width.ToString(CultureInfo.InvariantCulture),
                trickplayInfo.Height.ToString(CultureInfo.InvariantCulture),
                url);

            builder.AppendLine();
        }
    }

    /// <summary>
    /// Get the H.26X level of the output video stream.
    /// </summary>
    /// <param name="state">StreamState of the current stream.</param>
    /// <returns>H.26X level of the output video stream.</returns>
    private int? GetOutputVideoCodecLevel(StreamState state)
    {
        string levelString = string.Empty;
        if (EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
            && state.VideoStream is not null
            && state.VideoStream.Level.HasValue)
        {
            levelString = state.VideoStream.Level.Value.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            if (string.Equals(state.ActualOutputVideoCodec, "h264", StringComparison.OrdinalIgnoreCase))
            {
                levelString = state.GetRequestedLevel(state.ActualOutputVideoCodec) ?? "41";
                levelString = EncodingHelper.NormalizeTranscodingLevel(state, levelString);
            }

            if (string.Equals(state.ActualOutputVideoCodec, "h265", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.ActualOutputVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
            {
                levelString = state.GetRequestedLevel("h265") ?? state.GetRequestedLevel("hevc") ?? "120";
                levelString = EncodingHelper.NormalizeTranscodingLevel(state, levelString);
            }

            if (string.Equals(state.ActualOutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase))
            {
                levelString = state.GetRequestedLevel("av1") ?? "19";
                levelString = EncodingHelper.NormalizeTranscodingLevel(state, levelString);
            }
        }

        if (int.TryParse(levelString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLevel))
        {
            return parsedLevel;
        }

        return null;
    }

    /// <summary>
    /// Get the profile of the output video stream.
    /// </summary>
    /// <param name="state">StreamState of the current stream.</param>
    /// <param name="codec">Video codec.</param>
    /// <returns>Profile of the output video stream.</returns>
    private string GetOutputVideoCodecProfile(StreamState state, string codec)
    {
        string profileString = string.Empty;
        if (EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
            && !string.IsNullOrEmpty(state.VideoStream.Profile))
        {
            profileString = state.VideoStream.Profile;
        }
        else if (!string.IsNullOrEmpty(codec))
        {
            profileString = state.GetRequestedProfiles(codec).FirstOrDefault() ?? string.Empty;
            if (string.Equals(state.ActualOutputVideoCodec, "h264", StringComparison.OrdinalIgnoreCase))
            {
                profileString ??= "high";
            }

            if (string.Equals(state.ActualOutputVideoCodec, "h265", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.ActualOutputVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.ActualOutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase))
            {
                profileString ??= "main";
            }
        }

        // HDR passthrough: the bitstream is 10-bit HEVC Main10. The HLS CODECS attribute
        // must reflect this (hvc1.2.4.* rather than hvc1.1.6.*). HlsCodecStringHelpers
        // derives the profile_idc / compatibility / level fields from this string.
        if (EncodingHelper.IsHdrPassthroughMode(state)
            && (string.Equals(state.ActualOutputVideoCodec, "h265", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.ActualOutputVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)))
        {
            profileString = "main10";
        }

        return profileString;
    }

    /// <summary>
    /// Gets a formatted string of the output audio codec, for use in the CODECS field.
    /// </summary>
    /// <seealso cref="AppendPlaylistCodecsField(StringBuilder, StreamState)"/>
    /// <seealso cref="GetPlaylistVideoCodecs(StreamState, string, int)"/>
    /// <param name="state">StreamState of the current stream.</param>
    /// <returns>Formatted audio codec string.</returns>
    private string GetPlaylistAudioCodecs(StreamState state)
    {
        if (string.Equals(state.ActualOutputAudioCodec, "aac", StringComparison.OrdinalIgnoreCase))
        {
            string? profile = state.GetRequestedProfiles("aac").FirstOrDefault();
            return HlsCodecStringHelpers.GetAACString(profile);
        }

        if (string.Equals(state.ActualOutputAudioCodec, "mp3", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetMP3String();
        }

        if (string.Equals(state.ActualOutputAudioCodec, "ac3", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetAC3String();
        }

        if (string.Equals(state.ActualOutputAudioCodec, "eac3", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetEAC3String();
        }

        if (string.Equals(state.ActualOutputAudioCodec, "flac", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetFLACString();
        }

        if (string.Equals(state.ActualOutputAudioCodec, "alac", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetALACString();
        }

        if (string.Equals(state.ActualOutputAudioCodec, "opus", StringComparison.OrdinalIgnoreCase))
        {
            return HlsCodecStringHelpers.GetOPUSString();
        }

        return string.Empty;
    }

    /// <summary>
    /// Gets a formatted string of the output video codec, for use in the CODECS field.
    /// </summary>
    /// <seealso cref="AppendPlaylistCodecsField(StringBuilder, StreamState)"/>
    /// <seealso cref="GetPlaylistAudioCodecs(StreamState)"/>
    /// <param name="state">StreamState of the current stream.</param>
    /// <param name="codec">Video codec.</param>
    /// <param name="level">Video level.</param>
    /// <returns>Formatted video codec string.</returns>
    private string GetPlaylistVideoCodecs(StreamState state, string codec, int level)
    {
        if (level == 0)
        {
            // This is 0 when there's no requested level in the device profile
            // and the source is not encoded in H.26X or AV1
            _logger.LogError("Got invalid level when building CODECS field for HLS master playlist");
            return string.Empty;
        }

        if (string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase))
        {
            string profile = GetOutputVideoCodecProfile(state, "h264");
            return HlsCodecStringHelpers.GetH264String(profile, level);
        }

        if (string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase)
            || string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase))
        {
            string profile = GetOutputVideoCodecProfile(state, "hevc");
            return HlsCodecStringHelpers.GetH265String(profile, level);
        }

        if (string.Equals(codec, "av1", StringComparison.OrdinalIgnoreCase))
        {
            string profile = GetOutputVideoCodecProfile(state, "av1");

            // Currently we only transcode to 8 bits AV1
            int bitDepth = 8;
            if (EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
                && state.VideoStream is not null
                && state.VideoStream.BitDepth.HasValue)
            {
                bitDepth = state.VideoStream.BitDepth.Value;
            }

            return HlsCodecStringHelpers.GetAv1String(profile, level, false, bitDepth);
        }

        // VP9 HLS is for video remuxing only, everything is probed from the original video
        if (string.Equals(codec, "vp9", StringComparison.OrdinalIgnoreCase))
        {
            var width = state.VideoStream.Width ?? 0;
            var height = state.VideoStream.Height ?? 0;
            var framerate = state.VideoStream.ReferenceFrameRate ?? 30;
            var bitDepth = state.VideoStream.BitDepth ?? 8;
            return HlsCodecStringHelpers.GetVp9String(
                width,
                height,
                state.VideoStream.PixelFormat,
                framerate,
                bitDepth);
        }

        return string.Empty;
    }

    private int GetBitrateVariation(int bitrate)
    {
        // By default, vary by just 50k
        var variation = 50000;

        if (bitrate >= 10000000)
        {
            variation = 2000000;
        }
        else if (bitrate >= 5000000)
        {
            variation = 1500000;
        }
        else if (bitrate >= 3000000)
        {
            variation = 1000000;
        }
        else if (bitrate >= 2000000)
        {
            variation = 500000;
        }
        else if (bitrate >= 1000000)
        {
            variation = 300000;
        }
        else if (bitrate >= 600000)
        {
            variation = 200000;
        }
        else if (bitrate >= 400000)
        {
            variation = 100000;
        }

        return variation;
    }

    private string ReplacePlaylistCodecsField(StringBuilder playlist, StringBuilder oldValue, StringBuilder newValue)
    {
        var oldPlaylist = playlist.ToString();
        return oldPlaylist.Replace(
            oldValue.ToString(),
            newValue.ToString(),
            StringComparison.Ordinal);
    }
}
