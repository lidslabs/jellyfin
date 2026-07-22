using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Class TranscodingJob.
/// </summary>
public sealed class TranscodingJob : IDisposable
{
    private readonly ILogger<TranscodingJob> _logger;
    private readonly Lock _processLock = new();
    private readonly Lock _timerLock = new();

    private Timer? _killTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodingJob"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{TranscodingJobDto}"/> interface.</param>
    public TranscodingJob(ILogger<TranscodingJob> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets the play session identifier.
    /// </summary>
    public string? PlaySessionId { get; set; }

    /// <summary>
    /// Gets or sets the live stream identifier.
    /// </summary>
    public string? LiveStreamId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether is live output.
    /// </summary>
    public bool IsLiveOutput { get; set; }

    /// <summary>
    /// Gets or sets the path.
    /// </summary>
    public MediaSourceInfo? MediaSource { get; set; }

    /// <summary>
    /// Gets or sets path.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the type.
    /// </summary>
    public TranscodingJobType Type { get; set; }

    /// <summary>
    /// Gets or sets the process.
    /// </summary>
    public Process? Process { get; set; }

    /// <summary>
    /// Gets or sets the lidslabs NVEncC sidecar process (v0.4.0 muted-DV engine).
    /// When set, this decodes+encodes the DV video and pipes mpegts into the FIFO
    /// that the monitored ffmpeg (Process) stream-copies. It normally dies on its
    /// own via SIGPIPE when ffmpeg closes the FIFO, but that can lag a beat, so it
    /// is also explicitly killed on Stop to guarantee no orphaned GPU encoder.
    /// Null on all stock (non-NVEncC) jobs.
    /// </summary>
    public Process? LidslabsNvenccSidecar { get; set; }

    /// <summary>
    /// Gets or sets the lidslabs NVEncC FIFO path (v0.4.0), deleted on cleanup.
    /// </summary>
    public string? LidslabsNvenccFifoPath { get; set; }

    /// <summary>
    /// Gets or sets the active request count.
    /// </summary>
    public int ActiveRequestCount { get; set; }

    /// <summary>
    /// Gets or sets device id.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Gets or sets cancellation token source.
    /// </summary>
    public CancellationTokenSource? CancellationTokenSource { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether has exited.
    /// </summary>
    public bool HasExited { get; set; }

    /// <summary>
    /// Gets or sets exit code.
    /// </summary>
    public int ExitCode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether is user paused.
    /// </summary>
    public bool IsUserPaused { get; set; }

    /// <summary>
    /// Gets or sets id.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets framerate.
    /// </summary>
    public float? Framerate { get; set; }

    /// <summary>
    /// Gets or sets completion percentage.
    /// </summary>
    public double? CompletionPercentage { get; set; }

    /// <summary>
    /// Gets or sets bytes downloaded.
    /// </summary>
    public long BytesDownloaded { get; set; }

    /// <summary>
    /// Gets or sets bytes transcoded.
    /// </summary>
    public long? BytesTranscoded { get; set; }

    /// <summary>
    /// Gets or sets bit rate.
    /// </summary>
    public int? BitRate { get; set; }

    /// <summary>
    /// Gets or sets transcoding position ticks.
    /// </summary>
    public long? TranscodingPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets download position ticks.
    /// </summary>
    public long? DownloadPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets transcoding throttler.
    /// </summary>
    public TranscodingThrottler? TranscodingThrottler { get; set; }

    /// <summary>
    /// Gets or sets transcoding segment cleaner.
    /// </summary>
    public TranscodingSegmentCleaner? TranscodingSegmentCleaner { get; set; }

    /// <summary>
    /// Gets or sets last ping date.
    /// </summary>
    public DateTime LastPingDate { get; set; }

    /// <summary>
    /// Gets or sets ping timeout.
    /// </summary>
    public int PingTimeout { get; set; }

    /// <summary>
    /// Stop kill timer.
    /// </summary>
    public void StopKillTimer()
    {
        lock (_timerLock)
        {
            _killTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Dispose kill timer.
    /// </summary>
    public void DisposeKillTimer()
    {
        lock (_timerLock)
        {
            if (_killTimer is not null)
            {
                _killTimer.Dispose();
                _killTimer = null;
            }
        }
    }

    /// <summary>
    /// Start kill timer.
    /// </summary>
    /// <param name="callback">Callback action.</param>
    public void StartKillTimer(Action<object?> callback)
    {
        StartKillTimer(callback, PingTimeout);
    }

    /// <summary>
    /// Start kill timer.
    /// </summary>
    /// <param name="callback">Callback action.</param>
    /// <param name="intervalMs">Callback interval.</param>
    public void StartKillTimer(Action<object?> callback, int intervalMs)
    {
        if (HasExited)
        {
            return;
        }

        lock (_timerLock)
        {
            if (_killTimer is null)
            {
                _logger.LogDebug("Starting kill timer at {0}ms. JobId {1} PlaySessionId {2}", intervalMs, Id, PlaySessionId);
                _killTimer = new Timer(new TimerCallback(callback), this, intervalMs, Timeout.Infinite);
            }
            else
            {
                _logger.LogDebug("Changing kill timer to {0}ms. JobId {1} PlaySessionId {2}", intervalMs, Id, PlaySessionId);
                _killTimer.Change(intervalMs, Timeout.Infinite);
            }
        }
    }

    /// <summary>
    /// Change kill timer if started.
    /// </summary>
    public void ChangeKillTimerIfStarted()
    {
        if (HasExited)
        {
            return;
        }

        lock (_timerLock)
        {
            if (_killTimer is not null)
            {
                var intervalMs = PingTimeout;

                _logger.LogDebug("Changing kill timer to {0}ms. JobId {1} PlaySessionId {2}", intervalMs, Id, PlaySessionId);
                _killTimer.Change(intervalMs, Timeout.Infinite);
            }
        }
    }

    /// <summary>
    /// Stops the transcoding job.
    /// </summary>
    public void Stop()
    {
        lock (_processLock)
        {
#pragma warning disable CA1849 // Can't await in lock block
            TranscodingThrottler?.Stop().GetAwaiter().GetResult();
            TranscodingSegmentCleaner?.Stop();

            var process = Process;

            if (!HasExited)
            {
                try
                {
                    _logger.LogInformation("Stopping ffmpeg process with q command for {Path}", Path);

                    process!.StandardInput.WriteLine("q");

                    // Need to wait because killing is asynchronous.
                    if (!process.WaitForExit(5000))
                    {
                        _logger.LogInformation("Killing FFmpeg process for {Path}", Path);
                        process.Kill();
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }
#pragma warning restore CA1849

            // lidslabs v0.4.0: tear down the NVEncC sidecar. Closing ffmpeg's FIFO
            // read end above SIGPIPEs it, but only on its next write (can lag ~1-2s),
            // so kill it explicitly to guarantee the GPU encoder is released now.
            var sidecar = LidslabsNvenccSidecar;
            if (sidecar is not null)
            {
                try
                {
                    if (!sidecar.HasExited)
                    {
                        _logger.LogInformation("Killing lidslabs NVEncC sidecar for {Path}", Path);
                        sidecar.Kill();
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error killing lidslabs NVEncC sidecar for {Path}", Path);
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Process?.Dispose();
        Process = null;
        // lidslabs v0.4.0: dispose the NVEncC sidecar and remove its FIFO.
        LidslabsNvenccSidecar?.Dispose();
        LidslabsNvenccSidecar = null;
        if (!string.IsNullOrEmpty(LidslabsNvenccFifoPath))
        {
            try
            {
                if (File.Exists(LidslabsNvenccFifoPath))
                {
                    File.Delete(LidslabsNvenccFifoPath);
                }
            }
            catch (IOException)
            {
            }

            LidslabsNvenccFifoPath = null;
        }

        _killTimer?.Dispose();
        _killTimer = null;
        CancellationTokenSource?.Dispose();
        CancellationTokenSource = null;
        TranscodingThrottler?.Dispose();
        TranscodingThrottler = null;
        TranscodingSegmentCleaner?.Dispose();
        TranscodingSegmentCleaner = null;
    }
}
