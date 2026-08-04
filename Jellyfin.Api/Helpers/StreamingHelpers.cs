using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// The streaming helpers.
/// </summary>
public static class StreamingHelpers
{
    /// <summary>
    /// Gets the current streaming state.
    /// </summary>
    /// <param name="streamingRequest">The <see cref="StreamingRequestDto"/>.</param>
    /// <param name="httpContext">The <see cref="HttpContext"/>.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="encodingHelper">Instance of <see cref="EncodingHelper"/>.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="transcodingJobType">The <see cref="TranscodingJobType"/>.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>A <see cref="Task"/> containing the current <see cref="StreamState"/>.</returns>
    public static async Task<StreamState> GetStreamingState(
        StreamingRequestDto streamingRequest,
        HttpContext httpContext,
        IMediaSourceManager mediaSourceManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IServerConfigurationManager serverConfigurationManager,
        IMediaEncoder mediaEncoder,
        EncodingHelper encodingHelper,
        ITranscodeManager transcodeManager,
        TranscodingJobType transcodingJobType,
        CancellationToken cancellationToken)
    {
        var httpRequest = httpContext.Request;
        if (!string.IsNullOrWhiteSpace(streamingRequest.Params))
        {
            ParseParams(streamingRequest);
        }

        streamingRequest.StreamOptions = ParseStreamOptions(httpRequest.Query);
        if (httpRequest.Path.Value is null)
        {
            throw new ResourceNotFoundException(nameof(httpRequest.Path));
        }

        var url = httpRequest.Path.Value.AsSpan().RightPart('.').ToString();

        if (string.IsNullOrEmpty(streamingRequest.AudioCodec))
        {
            streamingRequest.AudioCodec = encodingHelper.InferAudioCodec(url);
        }

        var state = new StreamState(mediaSourceManager, transcodingJobType, transcodeManager)
        {
            Request = streamingRequest,
            RequestedUrl = url,
            UserAgent = httpRequest.Headers[HeaderNames.UserAgent]
        };

        var userId = httpContext.User.GetUserId();
        if (!userId.IsEmpty())
        {
            state.User = userManager.GetUserById(userId);
        }

        if (state.IsVideoRequest && !string.IsNullOrWhiteSpace(state.Request.VideoCodec))
        {
            state.SupportedVideoCodecs = state.Request.VideoCodec.Split(',', StringSplitOptions.RemoveEmptyEntries);
            state.Request.VideoCodec = state.SupportedVideoCodecs.FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(streamingRequest.AudioCodec))
        {
            state.SupportedAudioCodecs = streamingRequest.AudioCodec.Split(',', StringSplitOptions.RemoveEmptyEntries);
            state.Request.AudioCodec = state.SupportedAudioCodecs.FirstOrDefault(mediaEncoder.CanEncodeToAudioCodec)
                                       ?? state.SupportedAudioCodecs.FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(streamingRequest.SubtitleCodec))
        {
            state.SupportedSubtitleCodecs = streamingRequest.SubtitleCodec.Split(',', StringSplitOptions.RemoveEmptyEntries);
            state.Request.SubtitleCodec = state.SupportedSubtitleCodecs.FirstOrDefault(mediaEncoder.CanEncodeToSubtitleCodec)
                                          ?? state.SupportedSubtitleCodecs.FirstOrDefault();
        }

        var item = libraryManager.GetItemById<BaseItem>(streamingRequest.Id)
            ?? throw new ResourceNotFoundException();

        state.IsInputVideo = item.MediaType == MediaType.Video;

        MediaSourceInfo? mediaSource = null;
        if (string.IsNullOrWhiteSpace(streamingRequest.LiveStreamId))
        {
            var currentJob = !string.IsNullOrWhiteSpace(streamingRequest.PlaySessionId)
                ? transcodeManager.GetTranscodingJob(streamingRequest.PlaySessionId)
                : null;

            if (currentJob is not null)
            {
                mediaSource = currentJob.MediaSource;
            }

            if (mediaSource is null)
            {
                var mediaSources = await mediaSourceManager.GetPlaybackMediaSources(libraryManager.GetItemById<BaseItem>(streamingRequest.Id), null, false, false, cancellationToken).ConfigureAwait(false);

                mediaSource = string.IsNullOrEmpty(streamingRequest.MediaSourceId)
                    ? mediaSources[0]
                    : mediaSources.FirstOrDefault(i => string.Equals(i.Id, streamingRequest.MediaSourceId, StringComparison.Ordinal));

                if (mediaSource is null && Guid.Parse(streamingRequest.MediaSourceId).Equals(streamingRequest.Id))
                {
                    mediaSource = mediaSources[0];
                }
            }
        }
        else
        {
            var liveStreamInfo = await mediaSourceManager.GetLiveStreamWithDirectStreamProvider(streamingRequest.LiveStreamId, cancellationToken).ConfigureAwait(false);
            mediaSource = liveStreamInfo.Item1;
            state.DirectStreamProvider = liveStreamInfo.Item2;

            // Cap the max bitrate when it is too high. This is usually due to ffmpeg is unable to probe the source liveTV streams' bitrate.
            if (mediaSource.FallbackMaxStreamingBitrate is not null && streamingRequest.VideoBitRate is not null)
            {
                streamingRequest.VideoBitRate = Math.Min(streamingRequest.VideoBitRate.Value, mediaSource.FallbackMaxStreamingBitrate.Value);
            }
        }

        // lidslabs: make the per-user RemoteClientBitrateLimit binding rather than advisory.
        // Must run BEFORE AttachMediaSourceInfo — that is where an absent VideoCodec gets
        // inferred, and for a static URL it infers "copy". See LidslabsEnforceRemoteBitrateLimit.
        var lidslabsRemoteBitrateCap = LidslabsEnforceRemoteBitrateLimit(
            state,
            streamingRequest,
            httpContext,
            serverConfigurationManager,
            mediaSource);

        var encodingOptions = serverConfigurationManager.GetEncodingOptions();

        encodingHelper.AttachMediaSourceInfo(state, encodingOptions, mediaSource, url);

        string? containerInternal = Path.GetExtension(state.RequestedUrl);

        if (string.IsNullOrEmpty(containerInternal)
            && (!string.IsNullOrWhiteSpace(streamingRequest.LiveStreamId)
                || (mediaSource != null && mediaSource.IsInfiniteStream)))
        {
            containerInternal = ".ts";
        }

        if (!string.IsNullOrEmpty(streamingRequest.Container))
        {
            containerInternal = streamingRequest.Container;
        }

        if (string.IsNullOrEmpty(containerInternal))
        {
            containerInternal = streamingRequest.Static ?
                StreamBuilder.NormalizeMediaSourceFormatIntoSingleContainer(state.InputContainer, null, DlnaProfileType.Audio)
                : GetOutputFileExtension(state, mediaSource);
        }

        var outputAudioCodec = streamingRequest.AudioCodec;
        state.OutputAudioCodec = outputAudioCodec;
        state.OutputContainer = (containerInternal ?? string.Empty).TrimStart('.');
        state.OutputAudioChannels = encodingHelper.GetNumAudioChannelsParam(state, state.AudioStream, state.OutputAudioCodec);
        if (EncodingHelper.LosslessAudioCodecs.Contains(outputAudioCodec))
        {
            state.OutputAudioBitrate = state.AudioStream.BitRate ?? 0;
        }
        else
        {
            state.OutputAudioBitrate = encodingHelper.GetAudioBitrateParam(streamingRequest.AudioBitRate, streamingRequest.AudioCodec, state.AudioStream, state.OutputAudioChannels) ?? 0;
        }

        if (outputAudioCodec.StartsWith("pcm_", StringComparison.Ordinal))
        {
            containerInternal = ".pcm";
        }

        if (state.VideoRequest is not null)
        {
            // lidslabs: second half of the RemoteClientBitrateLimit enforcement — clamp the
            // requested video bitrate to the cap. The client picks this number on the transcode
            // URL, so without a clamp it can simply ask for more than the admin allows; that is
            // the same "advisory" hole as static=true, one layer in. Deliberately mirrors
            // StreamBuilder's own negotiation math (max bitrate minus audio, 64 kbps floor) so a
            // cooperative client's URL survives this untouched and only an over-ask is reduced.
            // Placed before GetVideoBitrateParamValue and TryStreamCopy so both see the clamp —
            // CanStreamCopyVideo refuses a copy whose source exceeds request.VideoBitRate, which
            // is what stops a forced transcode from silently degrading back into a remux.
            if (lidslabsRemoteBitrateCap.HasValue)
            {
                var lidslabsVideoBudget = Math.Max(lidslabsRemoteBitrateCap.Value - (state.OutputAudioBitrate ?? 0), 64_000);
                var lidslabsRequestedBitrate = state.VideoRequest.VideoBitRate;

                if (!lidslabsRequestedBitrate.HasValue || lidslabsRequestedBitrate.Value > lidslabsVideoBudget)
                {
                    state.VideoRequest.VideoBitRate = lidslabsVideoBudget;
                }
            }

            state.OutputVideoCodec = state.Request.VideoCodec;
            state.OutputVideoBitrate = encodingHelper.GetVideoBitrateParamValue(state.VideoRequest, state.VideoStream, state.OutputVideoCodec);

            encodingHelper.TryStreamCopy(state);

            if (!EncodingHelper.IsCopyCodec(state.OutputVideoCodec) && state.OutputVideoBitrate.HasValue)
            {
                var isVideoResolutionNotRequested = !state.VideoRequest.Width.HasValue
                    && !state.VideoRequest.Height.HasValue
                    && !state.VideoRequest.MaxWidth.HasValue
                    && !state.VideoRequest.MaxHeight.HasValue;

                if (isVideoResolutionNotRequested
                    && state.VideoStream is not null
                    && state.VideoRequest.VideoBitRate.HasValue
                    && state.VideoStream.BitRate.HasValue
                    && state.VideoRequest.VideoBitRate.Value >= state.VideoStream.BitRate.Value)
                {
                    // Don't downscale the resolution if the width/height/MaxWidth/MaxHeight is not requested,
                    // and the requested video bitrate is greater than source video bitrate.
                    if (state.VideoStream.Width.HasValue || state.VideoStream.Height.HasValue)
                    {
                        state.VideoRequest.MaxWidth = state.VideoStream?.Width;
                        state.VideoRequest.MaxHeight = state.VideoStream?.Height;
                    }
                }
                else
                {
                    var h264EquivalentBitrate = EncodingHelper.ScaleBitrate(
                        state.OutputVideoBitrate.Value,
                        state.ActualOutputVideoCodec,
                        "h264");
                    var resolution = ResolutionNormalizer.Normalize(
                        state.VideoStream?.BitRate,
                        state.OutputVideoBitrate.Value,
                        h264EquivalentBitrate,
                        state.VideoRequest.MaxWidth,
                        state.VideoRequest.MaxHeight,
                        state.TargetFramerate);

                    state.VideoRequest.MaxWidth = resolution.MaxWidth;
                    state.VideoRequest.MaxHeight = resolution.MaxHeight;
                }
            }

            if (state.AudioStream is not null && !EncodingHelper.IsCopyCodec(state.OutputAudioCodec) && string.Equals(state.AudioStream.Codec, state.OutputAudioCodec, StringComparison.OrdinalIgnoreCase) && state.OutputAudioBitrate.HasValue)
            {
                state.OutputAudioCodec = state.SupportedAudioCodecs.Where(c => !EncodingHelper.LosslessAudioCodecs.Contains(c)).FirstOrDefault(mediaEncoder.CanEncodeToAudioCodec);
            }
        }

        var ext = string.IsNullOrWhiteSpace(state.OutputContainer)
            ? GetOutputFileExtension(state, mediaSource)
            : ("." + GetContainerFileExtension(state.OutputContainer));

        state.OutputFilePath = GetOutputFilePath(state, ext, serverConfigurationManager, streamingRequest.DeviceId, streamingRequest.PlaySessionId);

        return state;
    }

    // ============================================================
    // RemoteClientBitrateLimit enforcement (lidslabs custom)
    // ============================================================
    // PROBLEM: the per-user remote bitrate cap is ADVISORY. `GetMaxBitrate` has exactly one
    // call site — `MediaInfoHelper` inside PlaybackInfo negotiation — so the cap only ever
    // shapes the answer the server *offers*. Nothing on the streaming path consults it, and
    // `VideosController` takes the client's `static=true` query parameter verbatim with no
    // user, policy or bitrate check. A client that declines the negotiated answer and asks
    // for the file itself therefore bypasses the cap completely, and the admin ends up bound
    // by the client's settings rather than the reverse.
    //
    // Measured 2026-08-03, remote invited user with a 25 Mbps cap against a 59,840 kb/s 4K
    // remux, Neptune on Apple TV (default playback mode: direct play): every playback attempt
    // failed until the *user* manually switched their client to transcode and picked a bitrate
    // under the cap. The server was correct throughout — it logged
    // `RemoteClientBitrateLimit: 25000000, IsInLocalNetwork: False` and, once the client
    // cooperated, produced exactly the right ffmpeg. The cap never prevented a single failure;
    // it only shaped the transcode after the client opted in.
    //
    // FIX: enforce the cap where the bytes actually leave — the streaming path. Two holes:
    //   1. `static=true` on an over-cap source. Clear `Static` so the request falls through
    //      to the transcode pipeline, and pin a real output video codec: for a static URL
    //      `AttachMediaSourceInfo` infers "copy", which would remux the source at full
    //      bitrate and enforce nothing.
    //   2. A transcode requested above the cap. Clamped in `GetStreamingState` (above).
    //
    // NOT A NEPTUNE FIX. Any client can decline the negotiated answer; Neptune merely does it
    // by default, which is how this surfaced. There is deliberately no client matching here.
    //
    // Ships ON, not behind an env lever: `RemoteClientBitrateLimit` is an existing documented
    // policy the admin sets *expecting* it to bind. Enforcing it closes a hole rather than
    // adding a feature.
    //
    // SCOPE — video requests only. The audio path (`AudioController` / `AudioHelper`) shares
    // this function and reaches the same static branch, but forcing a transcode there cannot
    // honour a cap: the audio codec on a static URL is inferred from the container, so a FLAC
    // request would "transcode" FLAC to FLAC at the same bitrate — a pointless re-encode with
    // no bandwidth saving. A music file also cannot realistically exceed a video bitrate cap
    // (FLAC ~1 Mbps against caps in the tens of Mbps). Left alone on purpose.
    private static int? LidslabsEnforceRemoteBitrateLimit(
        StreamState state,
        StreamingRequestDto streamingRequest,
        HttpContext httpContext,
        IServerConfigurationManager serverConfigurationManager,
        MediaSourceInfo? mediaSource)
    {
        var user = state.User;
        if (user is null)
        {
            return null;
        }

        // Same precedence as MediaInfoHelper.GetMaxBitrate: the user policy wins, the
        // server-wide setting is the fallback. Keep the two in step.
        var cap = user.RemoteClientBitrateLimit ?? 0;
        if (cap <= 0)
        {
            cap = serverConfigurationManager.Configuration.RemoteClientBitrateLimit;
        }

        if (cap <= 0)
        {
            return null;
        }

        var services = httpContext.RequestServices;

        // Resolved from the request container rather than threaded in as parameters.
        // GetStreamingState is a static helper called from four classes (VideosController,
        // DynamicHlsController, DynamicHlsHelper, AudioHelper); adding two required
        // constructor dependencies to all of them for one gate is a much wider patch to carry
        // across upstream rebases than a two-line lookup contained here.
        var networkManager = services.GetRequiredService<INetworkManager>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StreamingHelpers));

        var remoteIp = httpContext.GetNormalizedRemoteIP();
        if (networkManager.IsInLocalNetwork(remoteIp))
        {
            return null;
        }

        if (!streamingRequest.Static || !state.IsVideoRequest)
        {
            return cap;
        }

        // Fail open on anything we cannot measure or cannot meaningfully re-encode. A cap that
        // guesses is worse than one that declines: LiveTV and remote sources have no reliable
        // source bitrate, and upstream's own IsBitrateLimitExceeded skips remote sources too.
        if (mediaSource is null
            || mediaSource.IsRemote
            || mediaSource.IsInfiniteStream
            || !string.IsNullOrWhiteSpace(streamingRequest.LiveStreamId)
            || !mediaSource.Bitrate.HasValue
            || mediaSource.Bitrate.Value <= cap)
        {
            return cap;
        }

        // Without transcode permission the forced request would come straight back out of
        // TryStreamCopy as a copy — same bytes, same bandwidth, plus a needless ffmpeg. Say so
        // instead of pretending the cap was applied.
        if (!user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding))
        {
            logger.LogWarning(
                "lidslabs: remote bitrate cap {Cap} cannot be enforced for user {User} on a {SourceBitrate} source - the user has no video transcoding permission, serving the direct stream",
                cap,
                user.Username,
                mediaSource.Bitrate.Value);

            return cap;
        }

        streamingRequest.Static = false;

        // The client's real DeviceProfile, cached from the capabilities it posted when its
        // session opened. A static URL carries no codec or range parameters, so without this
        // the forced transcode would be a guess — and the transcode a client will accept is
        // NOT the same as the file it will direct-play.
        var clientProfile = services.GetRequiredService<IDeviceManager>()
            .GetCapabilities(streamingRequest.DeviceId)?.DeviceProfile;

        if (string.IsNullOrEmpty(streamingRequest.VideoCodec))
        {
            var forcedVideoCodec = LidslabsForcedTranscodeVideoCodec(mediaSource, clientProfile, logger);
            streamingRequest.VideoCodec = forcedVideoCodec;
            state.SupportedVideoCodecs = [forcedVideoCodec];
        }

        // Range, second half of the same question. On a static URL nothing requests a range, so
        // `GetRequestedRangeTypes` is empty, so the v0.3.2 "honour an explicit SDR request"
        // escape hatch in IsHdrPassthroughMode never fires and HDR passthrough engages BY
        // DEFAULT. For Trident that is the right answer. For a client that declared it takes
        // SDR only, it is a server-produced HDR stream nobody asked for — the black screen this
        // project has chased for months — and neither the SDR companion rung nor
        // LIDSLABS_TRANSCODE_SDR_LADDER_CLIENTS can save it, because both live in
        // DynamicHlsHelper/MediaInfoController and neither runs on this path.
        //
        // So: request SDR unless the client declared an HDR range (or declared none at all,
        // which means unconstrained, not refused). Setting VideoRangeType is the same seam the
        // SDR companion rung uses, and it authors the filter chain and the manifest together,
        // so the encode and what we advertise cannot disagree.
        if (string.IsNullOrEmpty(streamingRequest.VideoRangeType)
            && !LidslabsClientCaps.DeclaresHdrOrIsUnconstrained(LidslabsClientCaps.DeclaredVideoRanges(clientProfile)))
        {
            streamingRequest.VideoRangeType = VideoRangeType.SDR.ToString();
        }

        logger.LogInformation(
            "lidslabs: remote bitrate cap {Cap} binds - forcing a transcode for user {User} from {RemoteIp}, source is {SourceBitrate} bps, client profile {Profile}, output video codec {VideoCodec}, requested range {Range}",
            cap,
            user.Username,
            remoteIp,
            mediaSource.Bitrate.Value,
            clientProfile?.Name ?? "(none cached)",
            streamingRequest.VideoCodec,
            string.IsNullOrEmpty(streamingRequest.VideoRangeType) ? "(unconstrained)" : streamingRequest.VideoRangeType);

        return cap;
    }

    /// <summary>
    /// Picks the output video codec for a transcode the server forced on a client that asked for
    /// a direct stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A static URL carries no <c>videoCodec</c> parameter, so the codec has to come from
    /// somewhere. The right source is the client's own <b>TranscodingProfiles</b> — what it will
    /// accept as a transcode target — selected by the operator's ranking and filtered to codecs
    /// that can carry the source's range. Identical terms to the ranked video-codec preference in
    /// <c>MediaInfoController</c>, deliberately: rank ∩ advertised ∩ preserves-range.
    /// </para>
    /// <para>
    /// <b>Not the source codec, and not the DirectPlay profiles.</b> "It asked to direct-play an
    /// HEVC file, so send it HEVC" is wrong, and wrong in the direction that produces a black
    /// screen with nothing in the logs: Neptune AV Player advertises HEVC for direct play and its
    /// only video TranscodingProfile is H.264 over HLS. Its own decoder handling a file is not a
    /// promise about what it will ingest from us.
    /// </para>
    /// <para>
    /// The source codec remains the fallback for when no profile is cached — a server restart
    /// clears the capabilities map until the client posts again, and some clients never post at
    /// all. That case is logged, because a guess that looks like a decision is how this project
    /// has shipped dead features before.
    /// </para>
    /// </remarks>
    /// <param name="mediaSource">The resolved media source.</param>
    /// <param name="clientProfile">The client's cached device profile, or <c>null</c>.</param>
    /// <param name="logger">Logger for the fallback path.</param>
    /// <returns>The codec to transcode to.</returns>
    private static string LidslabsForcedTranscodeVideoCodec(
        MediaSourceInfo mediaSource,
        DeviceProfile? clientProfile,
        ILogger logger)
    {
        var videoStream = mediaSource.VideoStream;
        var sourceRange = videoStream?.VideoRangeType ?? VideoRangeType.Unknown;
        var preservesRange = LidslabsClientCaps.CodecPreservesRange(sourceRange);

        var advertised = LidslabsClientCaps.AdvertisedTranscodeVideoCodecs(clientProfile);
        if (advertised.Length > 0)
        {
            var ranked = LidslabsEnv.List(LidslabsEnv.PreferredVideoCodec, LidslabsEnv.PreferredVideoCodecDefault);

            // Operator's ranking first, then anything else the client advertises, so a client
            // that only offers something unranked still gets a codec it asked for rather than
            // one we invented.
            var selected = ranked.FirstOrDefault(c => advertised.Contains(c, StringComparer.OrdinalIgnoreCase) && preservesRange(c))
                ?? advertised.FirstOrDefault(preservesRange)
                ?? advertised.FirstOrDefault();

            if (!string.IsNullOrEmpty(selected))
            {
                return selected;
            }
        }

        // No profile cached, or it advertised no video transcode target at all.
        var sourceCodec = videoStream?.Codec ?? string.Empty;
        var fallback =
            string.Equals(sourceCodec, "hevc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceCodec, "h265", StringComparison.OrdinalIgnoreCase)
                ? "hevc"
                : string.Equals(sourceCodec, "h264", StringComparison.OrdinalIgnoreCase)
                    ? "h264"
                    // Anything else (av1, vc1, mpeg2video, ...) is not something this server
                    // re-encodes to itself. h264 is the universal target, except on an HDR
                    // source where it would silently discard the range.
                    : preservesRange("h264") ? "h264" : "hevc";

        logger.LogWarning(
            "lidslabs: no cached device profile for device {DeviceId} - the forced transcode is falling back to the source codec {SourceCodec} -> {Fallback}. This is a guess, not a negotiated answer",
            mediaSource.Id,
            string.IsNullOrEmpty(sourceCodec) ? "(unknown)" : sourceCodec,
            fallback);

        return fallback;
    }

    /// <summary>
    /// Parses query parameters as StreamOptions.
    /// </summary>
    /// <param name="queryString">The query string.</param>
    /// <returns>A <see cref="Dictionary{String,String}"/> containing the stream options.</returns>
    private static Dictionary<string, string?> ParseStreamOptions(IQueryCollection queryString)
    {
        Dictionary<string, string?> streamOptions = new Dictionary<string, string?>();
        foreach (var param in queryString)
        {
            if (char.IsLower(param.Key[0]))
            {
                // This was probably not parsed initially and should be a StreamOptions
                // or the generated URL should correctly serialize it
                // TODO: This should be incorporated either in the lower framework for parsing requests
                streamOptions[param.Key] = param.Value;
            }
        }

        return streamOptions;
    }

    /// <summary>
    /// Gets the output file extension.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="mediaSource">The mediaSource.</param>
    /// <returns>System.String.</returns>
    private static string GetOutputFileExtension(StreamState state, MediaSourceInfo? mediaSource)
    {
        var ext = Path.GetExtension(state.RequestedUrl);
        if (!string.IsNullOrEmpty(ext))
        {
            return ext;
        }

        // Try to infer based on the desired video codec
        if (state.IsVideoRequest)
        {
            var videoCodec = state.Request.VideoCodec;

            if (string.Equals(videoCodec, "h264", StringComparison.OrdinalIgnoreCase))
            {
                return ".ts";
            }

            if (string.Equals(videoCodec, "hevc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(videoCodec, "av1", StringComparison.OrdinalIgnoreCase))
            {
                return ".mp4";
            }

            if (string.Equals(videoCodec, "theora", StringComparison.OrdinalIgnoreCase))
            {
                return ".ogv";
            }

            if (string.Equals(videoCodec, "vp8", StringComparison.OrdinalIgnoreCase)
                || string.Equals(videoCodec, "vp9", StringComparison.OrdinalIgnoreCase)
                || string.Equals(videoCodec, "vpx", StringComparison.OrdinalIgnoreCase))
            {
                return ".webm";
            }

            if (string.Equals(videoCodec, "wmv", StringComparison.OrdinalIgnoreCase))
            {
                return ".asf";
            }
        }
        else
        {
            // Try to infer based on the desired audio codec
            var audioCodec = state.Request.AudioCodec;

            if (string.Equals("aac", audioCodec, StringComparison.OrdinalIgnoreCase))
            {
                return ".aac";
            }

            if (string.Equals("mp3", audioCodec, StringComparison.OrdinalIgnoreCase))
            {
                return ".mp3";
            }

            if (string.Equals("vorbis", audioCodec, StringComparison.OrdinalIgnoreCase))
            {
                return ".ogg";
            }

            if (string.Equals("wma", audioCodec, StringComparison.OrdinalIgnoreCase))
            {
                return ".wma";
            }
        }

        // Fallback to the container of mediaSource
        if (!string.IsNullOrEmpty(mediaSource?.Container))
        {
            var idx = mediaSource.Container.IndexOf(',', StringComparison.OrdinalIgnoreCase);
            return '.' + (idx == -1 ? mediaSource.Container : mediaSource.Container[..idx]).Trim();
        }

        throw new InvalidOperationException("Failed to find an appropriate file extension");
    }

    /// <summary>
    /// Gets the output file path for transcoding.
    /// </summary>
    /// <param name="state">The current <see cref="StreamState"/>.</param>
    /// <param name="outputFileExtension">The file extension of the output file.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="deviceId">The device id.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <returns>The complete file path, including the folder, for the transcoding file.</returns>
    private static string GetOutputFilePath(StreamState state, string outputFileExtension, IServerConfigurationManager serverConfigurationManager, string? deviceId, string? playSessionId)
    {
        // lidslabs: include the resolved output codecs in the hash. A single play session can
        // advertise more than one server-side transcode variant (e.g. the HDR-passthrough master
        // carries an H.264 SDR companion rung so AVPlayer will start HDR playback). Those variants
        // share MediaPath/UserAgent/DeviceId/PlaySessionId, so without the codec they hash to the
        // same output path and clobber each other's fMP4 init segment (-1.mp4) — last writer wins,
        // leaving the client an avcC init in front of hvcC segments (or vice versa): frozen/black
        // video while stream-copied audio keeps playing. Keying on codec gives each variant its own
        // init/segment files. Seeks within one codec still hash to the same base path, so warm-join
        // and reuse are unaffected.
        var data = $"{state.MediaPath}-{state.UserAgent}-{deviceId!}-{playSessionId!}-{state.OutputVideoCodec}-{state.OutputAudioCodec}";

        var filename = data.GetMD5().ToString("N", CultureInfo.InvariantCulture);
        var ext = outputFileExtension.ToLowerInvariant();
        var folder = serverConfigurationManager.GetTranscodePath();

        return Path.Combine(folder, filename + ext);
    }

    /// <summary>
    /// Parses the parameters.
    /// </summary>
    /// <param name="request">The request.</param>
    private static void ParseParams(StreamingRequestDto request)
    {
        if (string.IsNullOrEmpty(request.Params))
        {
            return;
        }

        var vals = request.Params.Split(';');

        var videoRequest = request as VideoRequestDto;

        for (var i = 0; i < vals.Length; i++)
        {
            var val = vals[i];

            if (string.IsNullOrWhiteSpace(val))
            {
                continue;
            }

            switch (i)
            {
                case 0:
                    // DeviceProfileId
                    break;
                case 1:
                    request.DeviceId = val;
                    break;
                case 2:
                    request.MediaSourceId = val;
                    break;
                case 3:
                    request.Static = string.Equals("true", val, StringComparison.OrdinalIgnoreCase);
                    break;
                case 4:
                    if (videoRequest is not null && IsValidCodecName(val))
                    {
                        videoRequest.VideoCodec = val;
                    }

                    break;
                case 5:
                    if (IsValidCodecName(val))
                    {
                        request.AudioCodec = val;
                    }

                    break;
                case 6:
                    if (videoRequest is not null)
                    {
                        videoRequest.AudioStreamIndex = int.Parse(val, CultureInfo.InvariantCulture);
                    }

                    break;
                case 7:
                    if (videoRequest is not null)
                    {
                        videoRequest.SubtitleStreamIndex = int.Parse(val, CultureInfo.InvariantCulture);
                    }

                    break;
                case 8:
                    if (videoRequest is not null)
                    {
                        videoRequest.VideoBitRate = int.Parse(val, CultureInfo.InvariantCulture);
                    }

                    break;
                case 9:
                    request.AudioBitRate = int.Parse(val, CultureInfo.InvariantCulture);
                    break;
                case 10:
                    request.MaxAudioChannels = int.Parse(val, CultureInfo.InvariantCulture);
                    break;
                case 11:
                    if (videoRequest is not null)
                    {
                        videoRequest.MaxFramerate = float.Parse(val, CultureInfo.InvariantCulture);
                    }

                    break;
                case 12:
                    if (videoRequest is not null)
                    {
                        videoRequest.MaxWidth = int.Parse(val, CultureInfo.InvariantCulture);
                    }

                    break;
                case 13:
                    if (videoRequest is not null)
                    {
                        videoRequest.MaxHeight = int.Parse(val, CultureInfo.InvariantCulture);
                    }

                    break;
                case 14:
                    request.StartTimeTicks = long.Parse(val, CultureInfo.InvariantCulture);
                    break;
                case 15:
                    if (videoRequest is not null && Regex.IsMatch(val, EncodingHelper.LevelValidationRegexStr))
                    {
                        videoRequest.Level = val;
                    }

                    break;
                case 16:
                    if (videoRequest is not null)
                    {
                        videoRequest.MaxRefFrames = int.Parse(val, CultureInfo.InvariantCulture);
                    }

                    break;
                case 17:
                    if (videoRequest is not null)
                    {
                        videoRequest.MaxVideoBitDepth = int.Parse(val, CultureInfo.InvariantCulture);
                    }

                    break;
                case 18:
                    if (videoRequest is not null && IsValidCodecName(val))
                    {
                        videoRequest.Profile = val;
                    }

                    break;
                case 19:
                    // cabac no longer used
                    break;
                case 20:
                    request.PlaySessionId = val;
                    break;
                case 21:
                    // api_key
                    break;
                case 22:
                    request.LiveStreamId = val;
                    break;
                case 23:
                    // Duplicating ItemId because of MediaMonkey
                    break;
                case 24:
                    if (videoRequest is not null)
                    {
                        videoRequest.CopyTimestamps = string.Equals("true", val, StringComparison.OrdinalIgnoreCase);
                    }

                    break;
                case 25:
                    if (!string.IsNullOrWhiteSpace(val) && videoRequest is not null)
                    {
                        if (Enum.TryParse(val, out SubtitleDeliveryMethod method))
                        {
                            videoRequest.SubtitleMethod = method;
                        }
                    }

                    break;
                case 26:
                    request.TranscodingMaxAudioChannels = int.Parse(val, CultureInfo.InvariantCulture);
                    break;
                case 27:
                    if (videoRequest is not null)
                    {
                        videoRequest.EnableSubtitlesInManifest = string.Equals("true", val, StringComparison.OrdinalIgnoreCase);
                    }

                    break;
                case 28:
                    request.Tag = val;
                    break;
                case 29:
                    if (videoRequest is not null)
                    {
                        videoRequest.RequireAvc = string.Equals("true", val, StringComparison.OrdinalIgnoreCase);
                    }

                    break;
                case 30:
                    if (IsValidCodecName(val))
                    {
                        request.SubtitleCodec = val;
                    }

                    break;
                case 31:
                    if (videoRequest is not null)
                    {
                        videoRequest.RequireNonAnamorphic = string.Equals("true", val, StringComparison.OrdinalIgnoreCase);
                    }

                    break;
                case 32:
                    if (videoRequest is not null)
                    {
                        videoRequest.DeInterlace = string.Equals("true", val, StringComparison.OrdinalIgnoreCase);
                    }

                    break;
                case 33:
                    request.TranscodeReasons = val;
                    break;
            }
        }
    }

    private static bool IsValidCodecName(string val)
    {
        return EncodingHelper.ContainerValidationRegex().IsMatch(val);
    }

    /// <summary>
    /// Parses the container into its file extension.
    /// </summary>
    /// <param name="container">The container.</param>
    private static string? GetContainerFileExtension(string? container)
    {
        if (string.Equals(container, "mpegts", StringComparison.OrdinalIgnoreCase))
        {
            return "ts";
        }

        if (string.Equals(container, "matroska", StringComparison.OrdinalIgnoreCase))
        {
            return "mkv";
        }

        return container;
    }
}
