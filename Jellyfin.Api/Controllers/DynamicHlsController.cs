using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Attributes;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.StreamingDtos;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using Jellyfin.MediaEncoding.Hls.Playlist;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Dynamic hls controller.
/// </summary>
[Route("")]
[Authorize]
public class DynamicHlsController : BaseJellyfinApiController
{
    private const EncoderPreset DefaultVodEncoderPreset = EncoderPreset.veryfast;
    private const EncoderPreset DefaultEventEncoderPreset = EncoderPreset.superfast;
    private const TranscodingJobType TranscodingJobType = MediaBrowser.Controller.MediaEncoding.TranscodingJobType.Hls;

    private readonly Version _minFFmpegFlacInMp4 = new Version(6, 0);
    private readonly Version _minFFmpegX265BframeInFmp4 = new Version(7, 0, 1);
    private readonly Version _minFFmpegHlsSegmentOptions = new Version(5, 0);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IFileSystem _fileSystem;
    private readonly ITranscodeManager _transcodeManager;
    private readonly ILogger<DynamicHlsController> _logger;
    private readonly EncodingHelper _encodingHelper;
    private readonly IDynamicHlsPlaylistGenerator _dynamicHlsPlaylistGenerator;
    private readonly DynamicHlsHelper _dynamicHlsHelper;
    private readonly EncodingOptions _encodingOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicHlsController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{DynamicHlsController}"/> interface.</param>
    /// <param name="dynamicHlsHelper">Instance of <see cref="DynamicHlsHelper"/>.</param>
    /// <param name="encodingHelper">Instance of <see cref="EncodingHelper"/>.</param>
    /// <param name="dynamicHlsPlaylistGenerator">Instance of <see cref="IDynamicHlsPlaylistGenerator"/>.</param>
    public DynamicHlsController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IServerConfigurationManager serverConfigurationManager,
        IMediaEncoder mediaEncoder,
        IFileSystem fileSystem,
        ITranscodeManager transcodeManager,
        ILogger<DynamicHlsController> logger,
        DynamicHlsHelper dynamicHlsHelper,
        EncodingHelper encodingHelper,
        IDynamicHlsPlaylistGenerator dynamicHlsPlaylistGenerator)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _serverConfigurationManager = serverConfigurationManager;
        _mediaEncoder = mediaEncoder;
        _fileSystem = fileSystem;
        _transcodeManager = transcodeManager;
        _logger = logger;
        _dynamicHlsHelper = dynamicHlsHelper;
        _encodingHelper = encodingHelper;
        _dynamicHlsPlaylistGenerator = dynamicHlsPlaylistGenerator;

        _encodingOptions = serverConfigurationManager.GetEncodingOptions();
    }

    /// <summary>
    /// Gets a hls live stream.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="container">The audio container.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="maxWidth">Optional. The max width.</param>
    /// <param name="maxHeight">Optional. The max height.</param>
    /// <param name="enableSubtitlesInManifest">Optional. Whether to enable subtitles in the manifest.</param>
    /// <param name="enableAudioVbrEncoding">Optional. Whether to enable Audio Encoding.</param>
    /// <param name="alwaysBurnInSubtitleWhenTranscoding">Whether to always burn in subtitles when transcoding.</param>
    /// <response code="200">Hls live stream retrieved.</response>
    /// <returns>A <see cref="FileResult"/> containing the hls file.</returns>
    [HttpGet("Videos/{itemId}/live.m3u8")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    public async Task<ActionResult> GetLiveHlsStream(
        [FromRoute, Required] Guid itemId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? container,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] bool? breakOnNonKeyFrames,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery] [RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] int? maxWidth,
        [FromQuery] int? maxHeight,
        [FromQuery] bool? enableSubtitlesInManifest,
        [FromQuery] bool enableAudioVbrEncoding = true,
        [FromQuery] bool alwaysBurnInSubtitleWhenTranscoding = false)
    {
        VideoRequestDto streamingRequest = new VideoRequestDto
        {
            Id = itemId,
            Container = container,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            MaxHeight = maxHeight,
            MaxWidth = maxWidth,
            EnableSubtitlesInManifest = enableSubtitlesInManifest ?? true,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = alwaysBurnInSubtitleWhenTranscoding
        };

        // CTS lifecycle is managed internally.
        var cancellationTokenSource = new CancellationTokenSource();
        // Due to CTS.Token calling ThrowIfDisposed (https://github.com/dotnet/runtime/issues/29970) we have to "cache" the token
        // since it gets disposed when ffmpeg exits
        var cancellationToken = cancellationTokenSource.Token;
        var state = await StreamingHelpers.GetStreamingState(
                streamingRequest,
                HttpContext,
                _mediaSourceManager,
                _userManager,
                _libraryManager,
                _serverConfigurationManager,
                _mediaEncoder,
                _encodingHelper,
                _transcodeManager,
                TranscodingJobType,
                cancellationToken)
            .ConfigureAwait(false);

        TranscodingJob? job = null;
        var playlistPath = Path.ChangeExtension(state.OutputFilePath, ".m3u8");

        if (!System.IO.File.Exists(playlistPath))
        {
            using (await _transcodeManager.LockAsync(playlistPath, cancellationToken).ConfigureAwait(false))
            {
                if (!System.IO.File.Exists(playlistPath))
                {
                    // If the playlist doesn't already exist, startup ffmpeg
                    try
                    {
                        job = await _transcodeManager.StartFfMpeg(
                                state,
                                playlistPath,
                                GetCommandLineArguments(playlistPath, state, true, 0),
                                Request.HttpContext.User.GetUserId(),
                                TranscodingJobType,
                                cancellationTokenSource)
                            .ConfigureAwait(false);
                        job.IsLiveOutput = true;
                    }
                    catch
                    {
                        state.Dispose();
                        throw;
                    }

                    minSegments = state.MinSegments;
                    if (minSegments > 0)
                    {
                        await HlsHelpers.WaitForMinimumSegmentCount(playlistPath, minSegments, _logger, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        job ??= _transcodeManager.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);

        if (job is not null)
        {
            _transcodeManager.OnTranscodeEndRequest(job);
        }

        var playlistText = HlsHelpers.GetLivePlaylistText(playlistPath, state);

        return Content(playlistText, MimeTypes.GetMimeType("playlist.m3u8"));
    }

    /// <summary>
    /// Gets a video hls playlist stream.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="maxWidth">Optional. The maximum horizontal resolution of the encoded video.</param>
    /// <param name="maxHeight">Optional. The maximum vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="enableAdaptiveBitrateStreaming">Enable adaptive bitrate streaming.</param>
    /// <param name="enableTrickplay">Enable trickplay image playlists being added to master playlist.</param>
    /// <param name="enableAudioVbrEncoding">Whether to enable Audio Encoding.</param>
    /// <param name="alwaysBurnInSubtitleWhenTranscoding">Whether to always burn in subtitles when transcoding.</param>
    /// <response code="200">Video stream returned.</response>
    /// <returns>A <see cref="FileResult"/> containing the playlist file.</returns>
    [HttpGet("Videos/{itemId}/master.m3u8")]
    [HttpHead("Videos/{itemId}/master.m3u8", Name = "HeadMasterHlsVideoPlaylist")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    public async Task<ActionResult> GetMasterHlsVideoPlaylist(
        [FromRoute, Required] Guid itemId,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery, Required] string mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] bool? breakOnNonKeyFrames,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery] [RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? maxWidth,
        [FromQuery] int? maxHeight,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] bool enableAdaptiveBitrateStreaming = false,
        [FromQuery] bool enableTrickplay = true,
        [FromQuery] bool enableAudioVbrEncoding = true,
        [FromQuery] bool alwaysBurnInSubtitleWhenTranscoding = false)
    {
        var streamingRequest = new HlsVideoRequestDto
        {
            Id = itemId,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            EnableAdaptiveBitrateStreaming = enableAdaptiveBitrateStreaming,
            EnableTrickplay = enableTrickplay,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = alwaysBurnInSubtitleWhenTranscoding
        };

        return await _dynamicHlsHelper.GetMasterHlsPlaylist(TranscodingJobType, streamingRequest, enableAdaptiveBitrateStreaming).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets an audio hls playlist stream.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="maxStreamingBitrate">Optional. The maximum streaming bitrate.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="enableAdaptiveBitrateStreaming">Enable adaptive bitrate streaming.</param>
    /// <param name="enableAudioVbrEncoding">Optional. Whether to enable Audio Encoding.</param>
    /// <response code="200">Audio stream returned.</response>
    /// <returns>A <see cref="FileResult"/> containing the playlist file.</returns>
    [HttpGet("Audio/{itemId}/master.m3u8")]
    [HttpHead("Audio/{itemId}/master.m3u8", Name = "HeadMasterHlsAudioPlaylist")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    public async Task<ActionResult> GetMasterHlsAudioPlaylist(
        [FromRoute, Required] Guid itemId,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery, Required] string mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] bool? breakOnNonKeyFrames,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? maxStreamingBitrate,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery] [RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] bool enableAdaptiveBitrateStreaming = false,
        [FromQuery] bool enableAudioVbrEncoding = true)
    {
        var streamingRequest = new HlsAudioRequestDto
        {
            Id = itemId,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate ?? maxStreamingBitrate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            EnableAdaptiveBitrateStreaming = enableAdaptiveBitrateStreaming,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = false
        };

        return await _dynamicHlsHelper.GetMasterHlsPlaylist(TranscodingJobType, streamingRequest, enableAdaptiveBitrateStreaming).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a video stream using HTTP live streaming.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="maxWidth">Optional. The maximum horizontal resolution of the encoded video.</param>
    /// <param name="maxHeight">Optional. The maximum vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="enableAudioVbrEncoding">Optional. Whether to enable Audio Encoding.</param>
    /// <param name="alwaysBurnInSubtitleWhenTranscoding">Whether to always burn in subtitles when transcoding.</param>
    /// <response code="200">Video stream returned.</response>
    /// <returns>A <see cref="FileResult"/> containing the audio file.</returns>
    [HttpGet("Videos/{itemId}/main.m3u8")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    public async Task<ActionResult> GetVariantHlsVideoPlaylist(
        [FromRoute, Required] Guid itemId,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] bool? breakOnNonKeyFrames,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery] [RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? maxWidth,
        [FromQuery] int? maxHeight,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] bool enableAudioVbrEncoding = true,
        [FromQuery] bool alwaysBurnInSubtitleWhenTranscoding = false)
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var streamingRequest = new VideoRequestDto
        {
            Id = itemId,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = alwaysBurnInSubtitleWhenTranscoding
        };

        return await GetVariantPlaylistInternal(streamingRequest, cancellationTokenSource)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets an audio stream using HTTP live streaming.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="maxStreamingBitrate">Optional. The maximum streaming bitrate.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="enableAudioVbrEncoding">Optional. Whether to enable Audio Encoding.</param>
    /// <response code="200">Audio stream returned.</response>
    /// <returns>A <see cref="FileResult"/> containing the audio file.</returns>
    [HttpGet("Audio/{itemId}/main.m3u8")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    public async Task<ActionResult> GetVariantHlsAudioPlaylist(
        [FromRoute, Required] Guid itemId,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] bool? breakOnNonKeyFrames,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? maxStreamingBitrate,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery] [RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] bool enableAudioVbrEncoding = true)
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var streamingRequest = new StreamingRequestDto
        {
            Id = itemId,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate ?? maxStreamingBitrate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = false
        };

        return await GetVariantPlaylistInternal(streamingRequest, cancellationTokenSource)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a video stream using HTTP live streaming.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="container">The video container. Possible values are: ts, webm, asf, wmv, ogv, mp4, m4v, mkv, mpeg, mpg, avi, 3gp, wmv, wtv, m2ts, mov, iso, flv. </param>
    /// <param name="runtimeTicks">The position of the requested segment in ticks.</param>
    /// <param name="actualSegmentLengthTicks">The length of the requested segment in ticks.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The desired segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="maxWidth">Optional. The maximum horizontal resolution of the encoded video.</param>
    /// <param name="maxHeight">Optional. The maximum vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="enableAudioVbrEncoding">Optional. Whether to enable Audio Encoding.</param>
    /// <param name="alwaysBurnInSubtitleWhenTranscoding">Whether to always burn in subtitles when transcoding.</param>
    /// <response code="200">Video stream returned.</response>
    /// <returns>A <see cref="FileResult"/> containing the audio file.</returns>
    [HttpGet("Videos/{itemId}/hls1/{playlistId}/{segmentId}.{container}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesVideoFile]
    [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "playlistId", Justification = "Imported from ServiceStack")]
    public async Task<ActionResult> GetHlsVideoSegment(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] string playlistId,
        [FromRoute, Required] int segmentId,
        [FromRoute, Required] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string container,
        [FromQuery, Required] long runtimeTicks,
        [FromQuery, Required] long actualSegmentLengthTicks,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] bool? breakOnNonKeyFrames,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery] [RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? maxWidth,
        [FromQuery] int? maxHeight,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] bool enableAudioVbrEncoding = true,
        [FromQuery] bool alwaysBurnInSubtitleWhenTranscoding = false)
    {
        var streamingRequest = new VideoRequestDto
        {
            Id = itemId,
            CurrentRuntimeTicks = runtimeTicks,
            ActualSegmentLengthTicks = actualSegmentLengthTicks,
            Container = container,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = alwaysBurnInSubtitleWhenTranscoding
        };

        return await GetDynamicSegment(streamingRequest, segmentId)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a video stream using HTTP live streaming.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="container">The video container. Possible values are: ts, webm, asf, wmv, ogv, mp4, m4v, mkv, mpeg, mpg, avi, 3gp, wmv, wtv, m2ts, mov, iso, flv. </param>
    /// <param name="runtimeTicks">The position of the requested segment in ticks.</param>
    /// <param name="actualSegmentLengthTicks">The length of the requested segment in ticks.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="maxStreamingBitrate">Optional. The maximum streaming bitrate.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="enableAudioVbrEncoding">Optional. Whether to enable Audio Encoding.</param>
    /// <response code="200">Video stream returned.</response>
    /// <returns>A <see cref="FileResult"/> containing the audio file.</returns>
    [HttpGet("Audio/{itemId}/hls1/{playlistId}/{segmentId}.{container}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesAudioFile]
    [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "playlistId", Justification = "Imported from ServiceStack")]
    public async Task<ActionResult> GetHlsAudioSegment(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] string playlistId,
        [FromRoute, Required] int segmentId,
        [FromRoute, Required] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string container,
        [FromQuery, Required] long runtimeTicks,
        [FromQuery, Required] long actualSegmentLengthTicks,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] bool? breakOnNonKeyFrames,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? maxStreamingBitrate,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery] [RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] bool enableAudioVbrEncoding = true)
    {
        var streamingRequest = new StreamingRequestDto
        {
            Id = itemId,
            Container = container,
            CurrentRuntimeTicks = runtimeTicks,
            ActualSegmentLengthTicks = actualSegmentLengthTicks,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate ?? maxStreamingBitrate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = false
        };

        return await GetDynamicSegment(streamingRequest, segmentId)
            .ConfigureAwait(false);
    }

    private async Task<ActionResult> GetVariantPlaylistInternal(StreamingRequestDto streamingRequest, CancellationTokenSource cancellationTokenSource)
    {
        // lidslabs: state disposal is managed manually (not `using`) because the transcoder-splash
        // warm-start below transfers ownership of the state to the transcode job it launches.
        var state = await StreamingHelpers.GetStreamingState(
                streamingRequest,
                HttpContext,
                _mediaSourceManager,
                _userManager,
                _libraryManager,
                _serverConfigurationManager,
                _mediaEncoder,
                _encodingHelper,
                _transcodeManager,
                TranscodingJobType,
                cancellationTokenSource.Token)
            .ConfigureAwait(false);
        var lidslabsStateOwnedByWarmStart = false;
        try
        {
            var mediaSourceId = state.BaseRequest.MediaSourceId;
            double fps = state.TargetFramerate ?? 0.0f;

            int segmentLength = state.SegmentLength * 1000;

            // If video is transcoded and framerate is fractional (i.e. 23.976), we need to slightly adjust segment length
            if (!EncodingHelper.IsCopyCodec(state.OutputVideoCodec) && Math.Abs(fps - Math.Floor(fps + 0.001f)) > 0.001)
            {
                double nearestIntFramerate = Math.Ceiling(fps);
                segmentLength = (int)Math.Ceiling(segmentLength * (nearestIntFramerate / fps));
            }

            var request = new CreateMainPlaylistRequest(
                mediaSourceId is null ? null : Guid.Parse(mediaSourceId),
                state.MediaPath,
                segmentLength,
                state.RunTimeTicks ?? 0,
                state.Request.SegmentContainer ?? string.Empty,
                "hls1/main/",
                Request.QueryString.ToString(),
                EncodingHelper.IsCopyCodec(state.OutputVideoCodec));
            var playlist = _dynamicHlsPlaylistGenerator.CreateMainPlaylist(request);

            // lidslabs: transcoder splash — prepend instantly-served splash segments so the viewer
            // sees video immediately, and warm the real transcode so its startup ramp overlaps
            // splash playback. Only for video sessions that actually transcode; strict no-op when
            // LIDSLABS_TRANSCODE_SPLASH is unset.
            if (LidslabsSplashSourcePath() is not null
                && state.IsOutputVideo
                && !EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
            {
                var spliced = LidslabsTryApplySplash(playlist, state, Request.QueryString.ToString());
                if (spliced is not null)
                {
                    playlist = spliced;
                    lidslabsStateOwnedByWarmStart = LidslabsTryWarmTranscode(state);
                }
            }

            return new FileContentResult(Encoding.UTF8.GetBytes(playlist), MimeTypes.GetMimeType("playlist.m3u8"));
        }
        finally
        {
            if (!lidslabsStateOwnedByWarmStart)
            {
                state.Dispose();
            }
        }
    }

    private async Task<ActionResult> GetDynamicSegment(StreamingRequestDto streamingRequest, int segmentId)
    {
        if ((streamingRequest.StartTimeTicks ?? 0) > 0)
        {
            throw new ArgumentException("StartTimeTicks is not allowed.");
        }

        // CTS lifecycle is managed internally.
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        var state = await StreamingHelpers.GetStreamingState(
                streamingRequest,
                HttpContext,
                _mediaSourceManager,
                _userManager,
                _libraryManager,
                _serverConfigurationManager,
                _mediaEncoder,
                _encodingHelper,
                _transcodeManager,
                TranscodingJobType,
                cancellationToken)
            .ConfigureAwait(false);

        var playlistPath = Path.ChangeExtension(state.OutputFilePath, ".m3u8");

        var segmentPath = GetSegmentPath(state, playlistPath, segmentId);

        var segmentExtension = EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer);

        TranscodingJob? job;

        if (System.IO.File.Exists(segmentPath))
        {
            job = _transcodeManager.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
            _logger.LogDebug("returning {0} [it exists, try 1]", segmentPath);
            return await GetSegmentResult(state, playlistPath, segmentPath, segmentExtension, segmentId, job, cancellationToken).ConfigureAwait(false);
        }

        using (await _transcodeManager.LockAsync(playlistPath, cancellationToken).ConfigureAwait(false))
        {
            var startTranscoding = false;
            if (System.IO.File.Exists(segmentPath))
            {
                job = _transcodeManager.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
                _logger.LogDebug("returning {0} [it exists, try 2]", segmentPath);
                return await GetSegmentResult(state, playlistPath, segmentPath, segmentExtension, segmentId, job, cancellationToken).ConfigureAwait(false);
            }

            var currentTranscodingIndex = GetCurrentTranscodingIndex(playlistPath, segmentExtension);
            var segmentGapRequiringTranscodingChange = 24 / state.SegmentLength;

            if (segmentId == -1)
            {
                // lidslabs: transcoder splash — a warm-started job for this exact playlist may still
                // be ramping (no output files yet, so currentTranscodingIndex is null); adopt it
                // instead of killing and restarting it. Inert when LIDSLABS_TRANSCODE_SPLASH is unset.
                if (LidslabsHasActiveJob(playlistPath))
                {
                    _logger.LogDebug("lidslabs: adopting in-flight warm transcode for the fmp4 init request");
                }
                else
                {
                    _logger.LogDebug("Starting transcoding because fmp4 init file is being requested");
                    startTranscoding = true;
                    segmentId = 0;
                }
            }
            else if (currentTranscodingIndex is null)
            {
                // lidslabs: same adopt rule for the first media segment (see the init branch above).
                if (segmentId == 0 && LidslabsHasActiveJob(playlistPath))
                {
                    _logger.LogDebug("lidslabs: adopting in-flight warm transcode for segment 0");
                }
                else
                {
                    _logger.LogDebug("Starting transcoding because currentTranscodingIndex=null");
                    startTranscoding = true;
                }
            }
            else if (segmentId < currentTranscodingIndex.Value)
            {
                _logger.LogDebug("Starting transcoding because requestedIndex={0} and currentTranscodingIndex={1}", segmentId, currentTranscodingIndex);
                startTranscoding = true;
            }
            else if (segmentId - currentTranscodingIndex.Value > segmentGapRequiringTranscodingChange)
            {
                _logger.LogDebug("Starting transcoding because segmentGap is {0} and max allowed gap is {1}. requestedIndex={2}", segmentId - currentTranscodingIndex.Value, segmentGapRequiringTranscodingChange, segmentId);
                startTranscoding = true;
            }

            if (startTranscoding)
            {
                // If the playlist doesn't already exist, startup ffmpeg
                try
                {
                    await _transcodeManager.KillTranscodingJobs(streamingRequest.DeviceId, streamingRequest.PlaySessionId, p => false)
                        .ConfigureAwait(false);

                    if (currentTranscodingIndex.HasValue)
                    {
                        await DeleteLastFile(playlistPath, segmentExtension, 0).ConfigureAwait(false);
                    }

                    streamingRequest.StartTimeTicks = streamingRequest.CurrentRuntimeTicks;

                    state.WaitForPath = segmentPath;
                    job = await _transcodeManager.StartFfMpeg(
                        state,
                        playlistPath,
                        GetCommandLineArguments(playlistPath, state, false, segmentId),
                        Request.HttpContext.User.GetUserId(),
                        TranscodingJobType,
                        cancellationTokenSource).ConfigureAwait(false);
                }
                catch
                {
                    state.Dispose();
                    throw;
                }

                // await WaitForMinimumSegmentCount(playlistPath, 1, cancellationTokenSource.Token).ConfigureAwait(false);
            }
            else
            {
                job = _transcodeManager.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
                if (job?.TranscodingThrottler is not null)
                {
                    await job.TranscodingThrottler.UnpauseTranscoding().ConfigureAwait(false);
                }
            }
        }

        _logger.LogDebug("returning {0} [general case]", segmentPath);
        job ??= _transcodeManager.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
        return await GetSegmentResult(state, playlistPath, segmentPath, segmentExtension, segmentId, job, cancellationToken).ConfigureAwait(false);
    }

    private static double[] GetSegmentLengths(StreamState state)
        => GetSegmentLengthsInternal(state.RunTimeTicks ?? 0, state.SegmentLength);

    internal static double[] GetSegmentLengthsInternal(long runtimeTicks, int segmentlength)
    {
        var segmentLengthTicks = TimeSpan.FromSeconds(segmentlength).Ticks;
        var wholeSegments = runtimeTicks / segmentLengthTicks;
        var remainingTicks = runtimeTicks % segmentLengthTicks;

        var segmentsLen = wholeSegments + (remainingTicks == 0 ? 0 : 1);
        var segments = new double[segmentsLen];
        for (int i = 0; i < wholeSegments; i++)
        {
            segments[i] = segmentlength;
        }

        if (remainingTicks != 0)
        {
            segments[^1] = TimeSpan.FromTicks(remainingTicks).TotalSeconds;
        }

        return segments;
    }

    private string GetCommandLineArguments(string outputPath, StreamState state, bool isEventPlaylist, int startNumber)
    {
        var videoCodec = _encodingHelper.GetVideoEncoder(state, _encodingOptions);
        var threads = EncodingHelper.GetNumberOfThreads(state, _encodingOptions, videoCodec);

        if (state.BaseRequest.BreakOnNonKeyFrames)
        {
            // FIXME: this is actually a workaround, as ideally it really should be the client which decides whether non-keyframe
            //        breakpoints are supported; but current implementation always uses "ffmpeg input seeking" which is liable
            //        to produce a missing part of video stream before first keyframe is encountered, which may lead to
            //        awkward cases like a few starting HLS segments having no video whatsoever, which breaks hls.js
            _logger.LogInformation("Current HLS implementation doesn't support non-keyframe breaks but one is requested, ignoring that request");
            state.BaseRequest.BreakOnNonKeyFrames = false;
        }

        var mapArgs = state.IsOutputVideo ? _encodingHelper.GetMapArgs(state) : string.Empty;

        var directory = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath));
        var outputFileNameWithoutExtension = Path.GetFileNameWithoutExtension(outputPath);
        var outputPrefix = Path.Combine(directory, outputFileNameWithoutExtension);
        var outputExtension = EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer);
        var outputTsArg = outputPrefix + "%d" + outputExtension;

        var segmentFormat = string.Empty;
        var segmentContainer = outputExtension.TrimStart('.');
        var inputModifier = _encodingHelper.GetInputModifier(state, _encodingOptions, segmentContainer);
        var hlsArguments = $"-hls_playlist_type {(isEventPlaylist ? "event" : "vod")} -hls_list_size 0";

        if (string.Equals(segmentContainer, "ts", StringComparison.OrdinalIgnoreCase))
        {
            segmentFormat = "mpegts";
        }
        else if (string.Equals(segmentContainer, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            var outputFmp4HeaderArg = OperatingSystem.IsWindows() switch
            {
                // on Windows, the path of fmp4 header file needs to be configured
                true => " -hls_fmp4_init_filename \"" + outputPrefix + "-1" + outputExtension + "\"",
                // on Linux/Unix, ffmpeg generate fmp4 header file to m3u8 output folder
                false => " -hls_fmp4_init_filename \"" + outputFileNameWithoutExtension + "-1" + outputExtension + "\""
            };

            var useLegacySegmentOption = _mediaEncoder.EncoderVersion < _minFFmpegHlsSegmentOptions;

            if (state.VideoStream is not null && state.IsOutputVideo)
            {
                // fMP4 needs this flag to write the audio packet DTS/PTS including the initial delay into MOOF::TRAF::TFDT
                hlsArguments += $" {(useLegacySegmentOption ? "-hls_ts_options" : "-hls_segment_options")} movflags=+frag_discont";

                // NOTE: an earlier revision appended "+cmaf" here for HDR streams to
                // force a CMAF-compliant colr atom. That broke fMP4 init segment
                // generation with NVENC HEVC 10-bit (the client's request for
                // hls1/main/-1.mp4 was canceled and ffmpeg restarted in a loop,
                // producing ~1s playback then multi-second freezes). The standard
                // fMP4 muxer already writes the colr atom from the encoder's
                // -color_primaries / -color_trc / -colorspace args, so +cmaf was
                // both redundant and harmful. Do not re-add it.
            }

            segmentFormat = "fmp4" + outputFmp4HeaderArg;
        }
        else
        {
            _logger.LogError("Invalid HLS segment container: {SegmentContainer}, default to mpegts", segmentContainer);
            segmentFormat = "mpegts";
        }

        var maxMuxingQueueSize = _encodingOptions.MaxMuxingQueueSize > 128
            ? _encodingOptions.MaxMuxingQueueSize.ToString(CultureInfo.InvariantCulture)
            : "128";

        var baseUrlParam = string.Empty;
        if (isEventPlaylist)
        {
            baseUrlParam = string.Format(
                CultureInfo.InvariantCulture,
                " -hls_base_url \"hls/{0}/\"",
                Path.GetFileNameWithoutExtension(outputPath));
        }

        // Timestamp handling. Jellyfin defaults to "-copyts -avoid_negative_ts disabled"
        // for HLS seek accuracy. We use "make_zero" instead for HDR transcodes as a
        // defense against negative-timestamp issues that surfaced when this build
        // used fMP4 segments (fMP4 tfdt boxes are strictly unsigned; "disabled"
        // could wrap negative and crash ExoPlayer). We're back on MPEG-TS now and
        // TS has no tfdt box, so make_zero is largely defensive here - but it's
        // also harmless and the safest value if delivery ever changes back.
        var tsArgs = EncodingHelper.IsHdrPassthroughMode(state)
            ? "-copyts -avoid_negative_ts make_zero"
            : "-copyts -avoid_negative_ts disabled";

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} -map_metadata -1 -map_chapters -1 -threads {2} {3} {4} {5} {6} -max_muxing_queue_size {7} -f hls -max_delay 5000000 -hls_time {8} -hls_segment_type {9} -start_number {10}{11} -hls_segment_filename \"{12}\" {13} -y \"{14}\"",
            inputModifier,
            _encodingHelper.GetInputArgument(state, _encodingOptions, segmentContainer),
            threads,
            mapArgs,
            GetVideoArguments(state, startNumber, isEventPlaylist, segmentContainer),
            GetAudioArguments(state),
            tsArgs,
            maxMuxingQueueSize,
            state.SegmentLength.ToString(CultureInfo.InvariantCulture),
            segmentFormat,
            startNumber.ToString(CultureInfo.InvariantCulture),
            baseUrlParam,
            EncodingUtils.NormalizePath(outputTsArg),
            hlsArguments,
            EncodingUtils.NormalizePath(outputPath)).Trim();
    }

    /// <summary>
    /// Gets the audio arguments for transcoding.
    /// </summary>
    /// <param name="state">The <see cref="StreamState"/>.</param>
    /// <returns>The command line arguments for audio transcoding.</returns>
    private string GetAudioArguments(StreamState state)
    {
        if (state.AudioStream is null)
        {
            return string.Empty;
        }

        var audioCodec = _encodingHelper.GetAudioEncoder(state);
        var bitStreamArgs = _encodingHelper.GetAudioBitStreamArguments(state, state.Request.SegmentContainer, state.MediaSource.Container);

        // opus, dts, truehd and flac (in FFmpeg 5 and older) are experimental in mp4 muxer
        var strictArgs = string.Empty;
        var actualOutputAudioCodec = state.ActualOutputAudioCodec;
        if (string.Equals(actualOutputAudioCodec, "opus", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actualOutputAudioCodec, "dts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actualOutputAudioCodec, "truehd", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(actualOutputAudioCodec, "flac", StringComparison.OrdinalIgnoreCase)
                && _mediaEncoder.EncoderVersion < _minFFmpegFlacInMp4))
        {
            strictArgs = " -strict -2";
        }

        if (!state.IsOutputVideo)
        {
            var audioTranscodeParams = string.Empty;

            // -vn to drop any video streams
            audioTranscodeParams += "-vn";

            if (EncodingHelper.IsCopyCodec(audioCodec))
            {
                return audioTranscodeParams + " -acodec copy" + bitStreamArgs + strictArgs;
            }

            audioTranscodeParams += " -acodec " + audioCodec + bitStreamArgs + strictArgs;

            var audioBitrate = state.OutputAudioBitrate;
            var audioChannels = state.OutputAudioChannels;

            if (audioBitrate.HasValue && !EncodingHelper.LosslessAudioCodecs.Contains(state.ActualOutputAudioCodec, StringComparison.OrdinalIgnoreCase))
            {
                var vbrParam = _encodingHelper.GetAudioVbrModeParam(audioCodec, audioBitrate.Value, audioChannels ?? 2);
                if (_encodingOptions.EnableAudioVbr && state.EnableAudioVbrEncoding && vbrParam is not null)
                {
                    audioTranscodeParams += vbrParam;
                }
                else
                {
                    audioTranscodeParams += " -ab " + audioBitrate.Value.ToString(CultureInfo.InvariantCulture);
                }
            }

            if (audioChannels.HasValue)
            {
                audioTranscodeParams += " -ac " + audioChannels.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (state.OutputAudioSampleRate.HasValue)
            {
                audioTranscodeParams += " -ar " + state.OutputAudioSampleRate.Value.ToString(CultureInfo.InvariantCulture);
            }

            return audioTranscodeParams;
        }

        if (EncodingHelper.IsCopyCodec(audioCodec))
        {
            var videoCodec = _encodingHelper.GetVideoEncoder(state, _encodingOptions);
            var copyArgs = "-codec:a:0 copy" + bitStreamArgs + strictArgs;

            if (EncodingHelper.IsCopyCodec(videoCodec) && state.EnableBreakOnNonKeyFrames(videoCodec))
            {
                return copyArgs + " -copypriorss:a:0 0";
            }

            return copyArgs;
        }

        var args = "-codec:a:0 " + audioCodec + bitStreamArgs + strictArgs;

        var channels = state.OutputAudioChannels;

        var useDownMixAlgorithm = DownMixAlgorithmsHelper.AlgorithmFilterStrings.ContainsKey((_encodingOptions.DownMixStereoAlgorithm, DownMixAlgorithmsHelper.InferChannelLayout(state.AudioStream)));

        if (channels.HasValue
            && (channels.Value != 2
                || (state.AudioStream?.Channels is not null && !useDownMixAlgorithm)))
        {
            args += " -ac " + channels.Value;
        }

        var bitrate = state.OutputAudioBitrate;
        if (bitrate.HasValue && !EncodingHelper.LosslessAudioCodecs.Contains(actualOutputAudioCodec, StringComparison.OrdinalIgnoreCase))
        {
            var vbrParam = _encodingHelper.GetAudioVbrModeParam(audioCodec, bitrate.Value, channels ?? 2);
            if (_encodingOptions.EnableAudioVbr && state.EnableAudioVbrEncoding && vbrParam is not null)
            {
                args += vbrParam;
            }
            else
            {
                args += " -ab " + bitrate.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (state.OutputAudioSampleRate.HasValue)
        {
            args += " -ar " + state.OutputAudioSampleRate.Value.ToString(CultureInfo.InvariantCulture);
        }
        else if (state.AudioStream?.CodecTag is not null && state.AudioStream.CodecTag.Equals("ac-4", StringComparison.Ordinal))
        {
            // ac-4 audio tends to have a super weird sample rate that will fail most encoders
            // force resample it to 48KHz
            args += " -ar 48000";
        }

        args += _encodingHelper.GetAudioFilterParam(state, _encodingOptions);

        return args;
    }

    /// <summary>
    /// Gets the video arguments for transcoding.
    /// </summary>
    /// <param name="state">The <see cref="StreamState"/>.</param>
    /// <param name="startNumber">The first number in the hls sequence.</param>
    /// <param name="isEventPlaylist">Whether the playlist is EVENT or VOD.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <returns>The command line arguments for video transcoding.</returns>
    private string GetVideoArguments(StreamState state, int startNumber, bool isEventPlaylist, string segmentContainer)
    {
        if (state.VideoStream is null)
        {
            return string.Empty;
        }

        if (!state.IsOutputVideo)
        {
            return string.Empty;
        }

        var codec = _encodingHelper.GetVideoEncoder(state, _encodingOptions);

        var args = "-codec:v:0 " + codec;

        var isActualOutputVideoCodecAv1 = string.Equals(state.ActualOutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase);
        var isActualOutputVideoCodecHevc = string.Equals(state.ActualOutputVideoCodec, "h265", StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(state.ActualOutputVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase);

        if (isActualOutputVideoCodecHevc || isActualOutputVideoCodecAv1)
        {
            var requestedRange = state.GetRequestedRangeTypes(state.ActualOutputVideoCodec);
            // Clients reporting Dolby Vision capabilities with fallbacks may only support the fallback layer.
            // Only enable Dolby Vision remuxing if the client explicitly declares support for profiles without fallbacks.
            var clientSupportsDoVi = requestedRange.Contains(VideoRangeType.DOVI.ToString(), StringComparison.OrdinalIgnoreCase);
            var videoIsDoVi = EncodingHelper.IsDovi(state.VideoStream);

            if (EncodingHelper.IsCopyCodec(codec)
                && (videoIsDoVi && clientSupportsDoVi)
                && !_encodingHelper.IsDoviRemoved(state))
            {
                if (isActualOutputVideoCodecHevc)
                {
                    // Use hvc1 for 8.4. This is what Dolby uses for its official sample streams. Tagging with dvh1 would break some players with strict tag checking like Apple Safari.
                    var codecTag = state.VideoStream.VideoRangeType == VideoRangeType.DOVIWithHLG ? "hvc1" : "dvh1";
                    args += $" -tag:v:0 {codecTag} -strict -2";
                }
                else if (isActualOutputVideoCodecAv1)
                {
                    args += " -tag:v:0 dav1 -strict -2";
                }
            }
            else if (isActualOutputVideoCodecHevc)
            {
                // Prefer hvc1 to hev1
                args += " -tag:v:0 hvc1";
            }
        }

        // if  (state.EnableMpegtsM2TsMode)
        // {
        //     args += " -mpegts_m2ts_mode 1";
        // }

        // See if we can save come cpu cycles by avoiding encoding.
        if (EncodingHelper.IsCopyCodec(codec))
        {
            // If h264_mp4toannexb is ever added, do not use it for live tv.
            if (state.VideoStream is not null && !string.Equals(state.VideoStream.NalLengthSize, "0", StringComparison.OrdinalIgnoreCase))
            {
                string bitStreamArgs = _encodingHelper.GetBitStreamArgs(state, MediaStreamType.Video);
                if (!string.IsNullOrEmpty(bitStreamArgs))
                {
                    args += " " + bitStreamArgs;
                }
            }

            args += " -start_at_zero";
        }
        else
        {
            args += _encodingHelper.GetVideoQualityParam(state, codec, _encodingOptions, isEventPlaylist ? DefaultEventEncoderPreset : DefaultVodEncoderPreset);

            // Set the key frame params for video encoding to match the hls segment time.
            args += _encodingHelper.GetHlsVideoKeyFrameArguments(state, codec, state.SegmentLength, isEventPlaylist, startNumber);

            // Currently b-frames in libx265 breaks the FMP4-HLS playback on iOS, disable it for now.
            if (string.Equals(codec, "libx265", StringComparison.OrdinalIgnoreCase)
                && _mediaEncoder.EncoderVersion < _minFFmpegX265BframeInFmp4)
            {
                args += " -bf 0";
            }

            // video processing filters.
            var videoProcessParam = _encodingHelper.GetVideoProcessingFilterParam(state, _encodingOptions, codec);

            var negativeMapArgs = _encodingHelper.GetNegativeMapArgsByFilters(state, videoProcessParam);

            args = negativeMapArgs + args + videoProcessParam;

            // -start_at_zero is necessary to use with -ss when seeking,
            // otherwise the target position cannot be determined.
            if (state.SubtitleStream is not null)
            {
                // Disable start_at_zero for external graphical subs
                if (!(state.SubtitleStream.IsExternal && !state.SubtitleStream.IsTextSubtitleStream))
                {
                    args += " -start_at_zero";
                }
            }
        }

        // TODO why was this not enabled for VOD?
        if (isEventPlaylist && string.Equals(segmentContainer, "ts", StringComparison.OrdinalIgnoreCase))
        {
            args += " -flags -global_header";
        }

        if (!string.IsNullOrEmpty(state.OutputVideoSync))
        {
            args += EncodingHelper.GetVideoSyncOption(state.OutputVideoSync, _mediaEncoder.EncoderVersion);
        }

        args += _encodingHelper.GetOutputFFlags(state);

        return args;
    }

    private string GetSegmentPath(StreamState state, string playlist, int index)
    {
        var folder = Path.GetDirectoryName(playlist) ?? throw new ArgumentException($"Provided path ({playlist}) is not valid.", nameof(playlist));
        var filename = Path.GetFileNameWithoutExtension(playlist);

        return Path.Combine(folder, filename + index.ToString(CultureInfo.InvariantCulture) + EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer));
    }

    private async Task<ActionResult> GetSegmentResult(
        StreamState state,
        string playlistPath,
        string segmentPath,
        string segmentExtension,
        int segmentIndex,
        TranscodingJob? transcodingJob,
        CancellationToken cancellationToken)
    {
        var segmentExists = System.IO.File.Exists(segmentPath);
        if (segmentExists)
        {
            if (transcodingJob is not null && transcodingJob.HasExited)
            {
                // Transcoding job is over, so assume all existing files are ready
                _logger.LogDebug("serving up {0} as transcode is over", segmentPath);
                return GetSegmentResult(state, segmentPath, transcodingJob);
            }

            var currentTranscodingIndex = GetCurrentTranscodingIndex(playlistPath, segmentExtension);

            // If requested segment is less than transcoding position, we can't transcode backwards, so assume it's ready
            if (segmentIndex < currentTranscodingIndex)
            {
                _logger.LogDebug("serving up {0} as transcode index {1} is past requested point {2}", segmentPath, currentTranscodingIndex, segmentIndex);
                return GetSegmentResult(state, segmentPath, transcodingJob);
            }
        }

        var nextSegmentPath = GetSegmentPath(state, playlistPath, segmentIndex + 1);
        if (transcodingJob is not null)
        {
            while (!cancellationToken.IsCancellationRequested && !transcodingJob.HasExited)
            {
                // To be considered ready, the segment file has to exist AND
                // either the transcoding job should be done or next segment should also exist
                if (segmentExists)
                {
                    if (transcodingJob.HasExited || System.IO.File.Exists(nextSegmentPath))
                    {
                        _logger.LogDebug("Serving up {SegmentPath} as it deemed ready", segmentPath);
                        return GetSegmentResult(state, segmentPath, transcodingJob);
                    }
                }
                else
                {
                    segmentExists = System.IO.File.Exists(segmentPath);
                    if (segmentExists)
                    {
                        continue; // avoid unnecessary waiting if segment just became available
                    }
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (!System.IO.File.Exists(segmentPath))
            {
                _logger.LogWarning("cannot serve {0} as transcoding quit before we got there", segmentPath);
            }
            else
            {
                _logger.LogDebug("serving {0} as it's on disk and transcoding stopped", segmentPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        else
        {
            _logger.LogWarning("cannot serve {0} as it doesn't exist and no transcode is running", segmentPath);
        }

        return GetSegmentResult(state, segmentPath, transcodingJob);
    }

    private ActionResult GetSegmentResult(StreamState state, string segmentPath, TranscodingJob? transcodingJob)
    {
        var segmentEndingPositionTicks = state.Request.CurrentRuntimeTicks + state.Request.ActualSegmentLengthTicks;

        Response.OnCompleted(() =>
        {
            _logger.LogDebug("Finished serving {SegmentPath}", segmentPath);
            if (transcodingJob is not null)
            {
                transcodingJob.DownloadPositionTicks = Math.Max(transcodingJob.DownloadPositionTicks ?? segmentEndingPositionTicks, segmentEndingPositionTicks);
                _transcodeManager.OnTranscodeEndRequest(transcodingJob);
            }

            return Task.CompletedTask;
        });

        return FileStreamResponseHelpers.GetStaticFileResult(segmentPath, MimeTypes.GetMimeType(segmentPath));
    }

    private int? GetCurrentTranscodingIndex(string playlist, string segmentExtension)
    {
        var job = _transcodeManager.GetTranscodingJob(playlist, TranscodingJobType);

        if (job is null || job.HasExited)
        {
            return null;
        }

        var file = GetLastTranscodingFile(playlist, segmentExtension, _fileSystem);

        if (file is null)
        {
            return null;
        }

        var playlistFilename = Path.GetFileNameWithoutExtension(playlist.AsSpan());

        var indexString = Path.GetFileNameWithoutExtension(file.Name.AsSpan()).Slice(playlistFilename.Length);

        return int.Parse(indexString, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static FileSystemMetadata? GetLastTranscodingFile(string playlist, string segmentExtension, IFileSystem fileSystem)
    {
        var folder = Path.GetDirectoryName(playlist) ?? throw new ArgumentException("Path can't be a root directory.", nameof(playlist));

        var filePrefix = Path.GetFileNameWithoutExtension(playlist);

        try
        {
            return fileSystem.GetFiles(folder, new[] { segmentExtension }, true, false)
                .Where(i => Path.GetFileNameWithoutExtension(i.Name).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                // Defensive filter: exclude any init segment named "<prefix>-1<ext>".
                // Under MPEG-TS delivery (the current default) no init segment is
                // produced, so this is a no-op. Kept for safety: if delivery is ever
                // switched to fMP4, the init segment's "-1" suffix would parse to
                // index -1 and poison GetCurrentTranscodingIndex - the negative
                // index makes the throttle/gap logic think the transcode is
                // perpetually behind, causing Jellyfin to kill/respawn ffmpeg in a
                // loop (which trips NVDEC errors as a downstream effect).
                // Media segments are always index >= 0; only the init segment would
                // be negative, so this filter is both safe under TS and protective
                // if fMP4 is ever re-enabled.
                .Where(i =>
                {
                    var indexSpan = Path.GetFileNameWithoutExtension(i.Name.AsSpan()).Slice(filePrefix.Length);
                    return int.TryParse(indexSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0;
                })
                .MaxBy(fileSystem.GetLastWriteTimeUtc);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task DeleteLastFile(string playlistPath, string segmentExtension, int retryCount)
    {
        var file = GetLastTranscodingFile(playlistPath, segmentExtension, _fileSystem);

        if (file is null)
        {
            return;
        }

        await DeleteFile(file.FullName, retryCount).ConfigureAwait(false);
    }

    private async Task DeleteFile(string path, int retryCount)
    {
        if (retryCount >= 5)
        {
            return;
        }

        _logger.LogDebug("Deleting partial HLS file {Path}", path);

        try
        {
            _fileSystem.DeleteFile(path);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path);

            await Task.Delay(100).ConfigureAwait(false);
            await DeleteFile(path, retryCount + 1).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path);
        }
    }

    // ====================================================================================
    // lidslabs v0.4.0: transcoder splash
    //
    // When LIDSLABS_TRANSCODE_SPLASH points at an operator-provided video file, transcoded
    // HLS video sessions get pre-encoded splash segments prepended to the variant playlist
    // (separated from the real content by #EXT-X-DISCONTINUITY). The splash serves
    // instantly from a per-profile rendition cache, masking the transcode-start ramp; the
    // real transcode is warm-started in parallel so the ramp overlaps splash playback.
    // Splash renditions match the session's output container, video codec, audio codec and
    // dynamic range (libavformat-based players keep one decoder across a discontinuity, so
    // codec parameters must not change at the seam). Every failure path falls through to
    // the stock playlist: the splash must never block or break playback.
    // ====================================================================================

    private static readonly ConcurrentDictionary<string, byte> _lidslabsSplashGenerating = new();

    /// <summary>
    /// Gets a transcoder-splash segment (lidslabs). Active only when LIDSLABS_TRANSCODE_SPLASH is set.
    /// </summary>
    /// <param name="itemId">The item id (route-shape compatibility; the splash is global).</param>
    /// <param name="segmentId">The segment id, or -1 for the fMP4 init segment.</param>
    /// <param name="container">The segment container.</param>
    /// <param name="lidslabsSplashKey">The splash rendition key.</param>
    /// <response code="200">Splash segment returned.</response>
    /// <response code="404">Splash disabled or segment not found.</response>
    /// <returns>A <see cref="FileResult"/> containing the splash segment.</returns>
    [HttpGet("Videos/{itemId}/hls1/splash/{segmentId}.{container}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesVideoFile]
    [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "itemId", Justification = "Route-shape parity with hls1/main segment URLs")]
    public ActionResult GetLidslabsSplashSegment(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] int segmentId,
        [FromRoute, Required] [RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string container,
        [FromQuery] string? lidslabsSplashKey)
    {
        if (LidslabsSplashSourcePath() is null
            || string.IsNullOrEmpty(lidslabsSplashKey)
            || !LidslabsIsValidSplashKey(lidslabsSplashKey))
        {
            return NotFound();
        }

        var fileName = segmentId == -1
            ? "init.mp4"
            : "s" + segmentId.ToString(CultureInfo.InvariantCulture) + "." + container;
        var path = Path.Combine(LidslabsSplashCacheRoot(), lidslabsSplashKey, fileName);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return FileStreamResponseHelpers.GetStaticFileResult(path, MimeTypes.GetMimeType(path));
    }

    private static string? LidslabsSplashSourcePath()
    {
        var value = Environment.GetEnvironmentVariable("LIDSLABS_TRANSCODE_SPLASH");
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool LidslabsIsValidSplashKey(string key)
    {
        if (key.Length is 0 or > 64)
        {
            return false;
        }

        foreach (var c in key)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static string? LidslabsNormalizeCodec(string? codec)
    {
        if (string.IsNullOrEmpty(codec))
        {
            return null;
        }

        return codec.ToLowerInvariant() switch
        {
            "h264" or "avc" => "h264",
            "hevc" or "h265" => "hevc",
            "aac" => "aac",
            "ac3" or "ac-3" => "ac3",
            "eac3" or "ec-3" => "eac3",
            "mp3" => "mp3",
            _ => null
        };
    }

    private string LidslabsSplashCacheRoot()
        => Path.Combine(_serverConfigurationManager.ApplicationPaths.CachePath, "lidslabs-splash");

    private bool LidslabsHasActiveJob(string playlistPath)
    {
        if (LidslabsSplashSourcePath() is null)
        {
            return false;
        }

        var job = _transcodeManager.GetTranscodingJob(playlistPath, TranscodingJobType);
        return job is not null && !job.HasExited;
    }

    /// <summary>
    /// Returns the playlist with splash segments spliced in, or null when the splash cannot be
    /// applied to this session (unsupported output profile, rendition not generated yet, or any
    /// preparation failure). Never throws.
    /// </summary>
    private string? LidslabsTryApplySplash(string playlist, StreamState state, string queryString)
    {
        try
        {
            var sourcePath = LidslabsSplashSourcePath();
            if (sourcePath is null)
            {
                return null;
            }

            if (!System.IO.File.Exists(sourcePath))
            {
                _logger.LogWarning("lidslabs: transcoder splash source not found at {Path}; skipping splash", sourcePath);
                return null;
            }

            var videoCodec = LidslabsNormalizeCodec(state.ActualOutputVideoCodec);
            var audioCodec = LidslabsNormalizeCodec(state.ActualOutputAudioCodec);
            if (videoCodec is not ("h264" or "hevc") || audioCodec is not ("aac" or "ac3" or "eac3" or "mp3"))
            {
                _logger.LogDebug("lidslabs: no splash rendition profile for v={Video} a={Audio}", state.ActualOutputVideoCodec, state.ActualOutputAudioCodec);
                return null;
            }

            // The HDR-passthrough gate only passes hevc/av1 outputs, so h264 is always sdr here.
            var range = "sdr";
            if (EncodingHelper.IsHdrPassthroughMode(state))
            {
                var sourceRange = state.VideoStream?.VideoRangeType ?? VideoRangeType.Unknown;
                range = sourceRange is VideoRangeType.HLG or VideoRangeType.DOVIWithHLG ? "hlg" : "pq";
            }

            var containerName = EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer).TrimStart('.');
            if (containerName is not ("ts" or "mp4"))
            {
                return null;
            }

            var key = string.Create(
                CultureInfo.InvariantCulture,
                $"{videoCodec}-{audioCodec}-{range}-{containerName}-s{state.SegmentLength}");
            var renditionDir = Path.Combine(LidslabsSplashCacheRoot(), key);
            var renditionPlaylistPath = Path.Combine(renditionDir, "splash.m3u8");
            var stampPath = Path.Combine(renditionDir, "source.stamp");
            var sourceInfo = new FileInfo(sourcePath);
            var stamp = string.Create(
                CultureInfo.InvariantCulture,
                $"{sourceInfo.Length}:{sourceInfo.LastWriteTimeUtc.Ticks}");

            if (!System.IO.File.Exists(renditionPlaylistPath)
                || !System.IO.File.Exists(stampPath)
                || !string.Equals(System.IO.File.ReadAllText(stampPath), stamp, StringComparison.Ordinal))
            {
                // Not cached (or the operator swapped the splash file): generate in the background
                // and let this session play without a splash; the next one picks it up.
                LidslabsStartRenditionGeneration(key, renditionDir, sourcePath, stamp, videoCodec, audioCodec, range, containerName, state.SegmentLength);
                return null;
            }

            return LidslabsSpliceSplash(playlist, renditionPlaylistPath, key, containerName, queryString);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lidslabs: transcoder splash preparation failed; serving the stock playlist");
            return null;
        }
    }

    private string? LidslabsSpliceSplash(string playlist, string renditionPlaylistPath, string key, string containerName, string queryString)
    {
        // Collect the rendition's segment durations (its files are named s0, s1, ... by construction).
        var durations = new List<double>();
        var lines = System.IO.File.ReadAllLines(renditionPlaylistPath);
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var durationText = line["#EXTINF:".Length..].Split(',')[0];
                if (double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                {
                    durations.Add(duration);
                }
            }
        }

        if (durations.Count == 0)
        {
            return null;
        }

        var anchor = playlist.IndexOf("#EXT-X-MEDIA-SEQUENCE", StringComparison.Ordinal);
        if (anchor < 0)
        {
            return null;
        }

        var insertAt = playlist.IndexOf('\n', anchor);
        if (insertAt < 0)
        {
            return null;
        }

        insertAt++;

        // The content #EXT-X-MAP (fmp4) sits directly after #EXT-X-MEDIA-SEQUENCE, so inserting the
        // splash block at this point yields: splash map, splash segments, discontinuity, content map,
        // content segments — which is exactly the required ordering.
        var uriSuffix = (queryString.Length == 0 ? "?" : queryString + "&") + "lidslabsSplashKey=" + key;
        var builder = new StringBuilder(1024);
        if (string.Equals(containerName, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("#EXT-X-MAP:URI=\"hls1/splash/-1.mp4")
                .Append(uriSuffix)
                .Append('"')
                .AppendLine();
        }

        var index = 0;
        foreach (var duration in durations)
        {
            builder.Append("#EXTINF:")
                .Append(duration.ToString("0.000000", CultureInfo.InvariantCulture))
                .AppendLine(", nodesc")
                .Append("hls1/splash/")
                .Append(index++)
                .Append('.')
                .Append(containerName)
                .AppendLine(uriSuffix);
        }

        builder.AppendLine("#EXT-X-DISCONTINUITY");

        var spliced = playlist.Insert(insertAt, builder.ToString());

        // Bump TARGETDURATION if a splash segment exceeds the content's.
        const string TargetDurationTag = "#EXT-X-TARGETDURATION:";
        var splashMax = (int)Math.Ceiling(durations.Max());
        var tagStart = spliced.IndexOf(TargetDurationTag, StringComparison.Ordinal);
        if (tagStart >= 0)
        {
            var valueStart = tagStart + TargetDurationTag.Length;
            var valueEnd = spliced.IndexOf('\n', valueStart);
            if (valueEnd > valueStart)
            {
                var valueText = spliced[valueStart..valueEnd].TrimEnd('\r');
                if (int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current) && splashMax > current)
                {
                    spliced = spliced.Remove(valueStart, valueText.Length)
                        .Insert(valueStart, splashMax.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        _logger.LogInformation("lidslabs: transcoder splash applied (key={Key}, segments={Count})", key, durations.Count);
        return spliced;
    }

    /// <summary>
    /// Fire-and-forget warm start of the real transcode so its startup ramp runs while the client
    /// plays the splash. On success the transcode job owns the state; returns true when ownership
    /// of the state has been transferred to the background task (in every case), false when the
    /// caller must dispose the state itself.
    /// </summary>
    private bool LidslabsTryWarmTranscode(StreamState state)
    {
        try
        {
            var playlistPath = Path.ChangeExtension(state.OutputFilePath, ".m3u8");
            var commandLineArguments = GetCommandLineArguments(playlistPath, state, false, 0);
            var userId = Request.HttpContext.User.GetUserId();
            var deviceId = state.Request.DeviceId;
            var playSessionId = state.Request.PlaySessionId;

            _ = Task.Run(async () =>
            {
                var ownsState = true;
                try
                {
                    using (await _transcodeManager.LockAsync(playlistPath, CancellationToken.None).ConfigureAwait(false))
                    {
                        var existing = _transcodeManager.GetTranscodingJob(playlistPath, TranscodingJobType);
                        if (existing is not null && !existing.HasExited)
                        {
                            // A segment request beat us to it; that job already owns its own state.
                            return;
                        }

                        if (!string.IsNullOrEmpty(deviceId))
                        {
                            await _transcodeManager.KillTranscodingJobs(deviceId, playSessionId, _ => false).ConfigureAwait(false);
                        }

                        // Wait for segment 0 like the stock start path does — NOT for ffmpeg's own
                        // playlist file: with -hls_playlist_type vod ffmpeg only writes that at
                        // finalize, which would hold this lock for the entire transcode.
                        state.WaitForPath = GetSegmentPath(state, playlistPath, 0);
                        await _transcodeManager.StartFfMpeg(
                            state,
                            playlistPath,
                            commandLineArguments,
                            userId,
                            TranscodingJobType,
                            new CancellationTokenSource()).ConfigureAwait(false);
                        ownsState = false;
                        _logger.LogInformation("lidslabs: warm transcode started for {PlaylistPath}", playlistPath);

                        // Arm the idle kill-timer (begin+end leaves ActiveRequestCount at 0): if no
                        // client ever claims this warm job, it dies in the stock HLS idle window
                        // instead of transcoding the whole file for nobody.
                        var warmJob = _transcodeManager.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
                        if (warmJob is not null)
                        {
                            _transcodeManager.OnTranscodeEndRequest(warmJob);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "lidslabs: warm transcode failed to start; playback will start it lazily");
                }
                finally
                {
                    if (ownsState)
                    {
                        state.Dispose();
                    }
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lidslabs: warm transcode setup failed; playback will start it lazily");
            return false;
        }
    }

    private void LidslabsStartRenditionGeneration(string key, string renditionDir, string sourcePath, string stamp, string videoCodec, string audioCodec, string range, string containerName, int segmentLength)
    {
        if (!_lidslabsSplashGenerating.TryAdd(key, 0))
        {
            return;
        }

        var encoderPath = _mediaEncoder.EncoderPath;
        _ = Task.Run(async () =>
        {
            var tempDir = renditionDir + ".tmp";
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }

                Directory.CreateDirectory(tempDir);
                var arguments = LidslabsBuildRenditionArguments(sourcePath, tempDir, videoCodec, audioCodec, range, containerName, segmentLength);
                _logger.LogInformation("lidslabs: generating splash rendition {Key}: {Encoder} {Arguments}", key, encoderPath, arguments);

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = encoderPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var standardError = process.StandardError.ReadToEndAsync();
                _ = process.StandardOutput.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    _logger.LogWarning("lidslabs: splash rendition {Key} generation timed out", key);
                    return;
                }

                if (process.ExitCode != 0)
                {
                    var errorText = await standardError.ConfigureAwait(false);
                    _logger.LogWarning(
                        "lidslabs: splash rendition {Key} generation failed (exit {Code}): {Error}",
                        key,
                        process.ExitCode,
                        errorText.Length > 500 ? errorText[^500..] : errorText);
                    return;
                }

                await System.IO.File.WriteAllTextAsync(Path.Combine(tempDir, "source.stamp"), stamp).ConfigureAwait(false);
                if (Directory.Exists(renditionDir))
                {
                    Directory.Delete(renditionDir, true);
                }

                Directory.Move(tempDir, renditionDir);
                _logger.LogInformation("lidslabs: splash rendition {Key} ready", key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "lidslabs: splash rendition {Key} generation failed", key);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
                catch (IOException)
                {
                }

                _lidslabsSplashGenerating.TryRemove(key, out _);
            }
        });
    }

    private static string LidslabsBuildRenditionArguments(string sourcePath, string outputDir, string videoCodec, string audioCodec, string range, string containerName, int segmentLength)
    {
        // 1080p letterboxed canvas; splash resolution/framerate need not match the content
        // (the discontinuity covers that), but codec family and dynamic range must.
        var filters = "scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1920:1080:(ow-iw)/2:(oh-ih)/2";
        string videoArguments;
        if (string.Equals(videoCodec, "h264", StringComparison.Ordinal))
        {
            filters += ",format=yuv420p";
            videoArguments = "-c:v libx264 -preset veryfast -b:v 6000k -maxrate 7000k -bufsize 12000k -profile:v high -sc_threshold 0";
        }
        else
        {
            videoArguments = "-c:v libx265 -preset veryfast -b:v 6000k -x265-params scenecut=0:log-level=error";
            if (string.Equals(containerName, "mp4", StringComparison.Ordinal))
            {
                videoArguments += " -tag:v hvc1";
            }

            if (string.Equals(range, "sdr", StringComparison.Ordinal))
            {
                filters += ",format=yuv420p";
            }
            else
            {
                // Map the (SDR) splash into the HDR range the session delivers, so the seam
                // does not switch dynamic range. One zscale call with explicit input tags —
                // zimg linearizes internally; a multi-step linear/RGB chain fails with
                // "no path between colorspaces". SDR reference white lands at 203 nits.
                var transfer = string.Equals(range, "hlg", StringComparison.Ordinal) ? "arib-std-b67" : "smpte2084";
                filters += ",zscale=tin=bt709:pin=bt709:min=bt709:rin=tv:t=" + transfer + ":p=bt2020:m=bt2020nc:r=tv:npl=203,format=yuv420p10le";
                videoArguments += " -color_primaries bt2020 -color_trc " + transfer + " -colorspace bt2020nc";
            }
        }

        var segmentType = string.Equals(containerName, "mp4", StringComparison.Ordinal)
            ? "fmp4 -hls_fmp4_init_filename init.mp4"
            : "mpegts";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"-hide_banner -loglevel error -nostdin -y -i \"{sourcePath}\" -t 30 -map 0:v:0 -map 0:a:0 -vf \"{filters}\" {videoArguments} -force_key_frames:v \"expr:gte(t,n_forced*{segmentLength})\" -c:a {audioCodec} -b:a 192k -ac 2 -ar 48000 -f hls -hls_time {segmentLength} -hls_playlist_type vod -hls_list_size 0 -hls_segment_type {segmentType} -hls_segment_filename \"{outputDir}/s%d.{containerName}\" \"{outputDir}/splash.m3u8\"");
    }
}
