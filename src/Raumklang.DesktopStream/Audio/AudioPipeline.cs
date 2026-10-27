using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Raumklang.DesktopStream.Audio;

public sealed class AudioPipeline : IDisposable
{
    private readonly Configuration _config;
    private readonly BufferedWaveProvider _pcm;
    private readonly StreamingMp3Encoder _encoder;
    private readonly Lock _gate = new();
    private MMDevice? _device;
    private WasapiRecorder? _recorder;
    private Exception? _captureError;
    private volatile bool _disposing;
    private long _droppedBytes;
    private volatile bool _capturing;
    public bool IsCapturing => _capturing;
    public string DeviceName { get; private set; } = "";
    public WaveFormat Format { get; }
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);
    public double EncoderFirstOutputInputMilliseconds => _encoder.FirstOutputInputMilliseconds;
    public double BufferedMilliseconds => _pcm.BufferedDuration.TotalMilliseconds;

    public static MMDevice SelectDevice(string selector)
    {
        using var devices = new MMDeviceEnumerator();
        if (selector == "default")
        {
            return devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        MMDevice? selected = null;
        foreach (MMDevice? candidate in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (candidate.ID.Equals(selector, StringComparison.OrdinalIgnoreCase) || candidate.FriendlyName.Equals(selector, StringComparison.OrdinalIgnoreCase))
            {
                if (selected != null)
                {
                    candidate.Dispose();
                    selected.Dispose();
                    throw new IOException("Audio device name is ambiguous; use its ID.");
                }

                selected = candidate;
            }
            else
            {
                candidate.Dispose();
            }
        }

        return selected ?? throw new IOException($"Audio rendering device not available: {selector}");
    }

    public AudioPipeline(Configuration config, Stream output)
    {
        _config = config;
        using MMDevice initial = SelectDevice(config.AudioDevice);
        using AudioClient audioClient = initial.CreateAudioClient();
        int rate = audioClient.MixFormat.SampleRate;
        Format = WaveFormat.CreateIeeeFloatWaveFormat(rate is 44100 or 48000 ? rate : 48000, 2);
        _pcm = new BufferedWaveProvider(Format, TimeSpan.FromMilliseconds(100))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _encoder = new StreamingMp3Encoder(output, Format, config.BitrateKbps);
        try
        {
            OpenCapture();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void OpenCapture()
    {
        MMDevice selected = SelectDevice(_config.AudioDevice);
        WasapiRecorder? next = null;
        try
        {
            next = new WasapiRecorderBuilder().WithDevice(selected).WithLoopbackCapture().WithFormat(Format).WithBufferLength(20).Build();
            next.DataAvailable += Capture;
            next.RecordingStopped += (_, args) =>
            {
                _capturing = false;
                if (!_disposing)
                {
                    Volatile.Write(ref _captureError, args.Exception ?? new IOException("WASAPI recording stopped unexpectedly."));
                }
            };
            next.StartRecording();
            _device = selected;
            _recorder = next;
            DeviceName = selected.FriendlyName;
            Volatile.Write(ref _captureError, null);
            _capturing = true;
            Log.Write($"WASAPI loopback: {DeviceName}; {Format.SampleRate} Hz stereo -> MP3 {_config.BitrateKbps} kbps");
        }
        catch
        {
            next?.Dispose();
            selected.Dispose();
            throw;
        }
    }

    private void Capture(ReadOnlySpan<byte> bytes, AudioClientBufferFlags flags, long position, long qpc)
    {
        if (_disposing || bytes.IsEmpty)
        {
            return;
        }

        // Copy NAudio's borrowed buffer before the callback returns.
        byte[] copy = bytes.ToArray();
        lock (_gate)
        {
            if (_disposing)
            {
                return;
            }

            if (_pcm.BufferedBytes + copy.Length > _pcm.BufferLength)
            {
                Interlocked.Add(ref _droppedBytes, _pcm.BufferedBytes);
                _pcm.ClearBuffer();
            }

            _pcm.AddSamples(copy, 0, copy.Length);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task encode = EncodeAsync(scope.Token);
        Task watch = WatchDevicesAsync(scope.Token);
        try
        {
            await await Task.WhenAny(encode, watch);
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("Audio worker stopped unexpectedly.");
        }
        finally
        {
            await scope.CancelAsync();
            try
            {
                await Task.WhenAll(encode, watch);
            }
            catch (OperationCanceledException) when (scope.IsCancellationRequested)
            {
            }
        }
    }

    private async Task EncodeAsync(CancellationToken cancellationToken)
    {
        // Read on the audio clock. The PCM buffer supplies silence on underrun.
        await Task.Delay(20, cancellationToken);
        using var clock = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        byte[] block = new byte[Format.SampleRate / 100 * Format.BlockAlign];
        var elapsed = Stopwatch.StartNew();
        long processedFrames = 0;
        int blockFrames = block.Length / Format.BlockAlign;
        while (await clock.WaitForNextTickAsync(cancellationToken))
        {
            long targetFrames = elapsed.ElapsedTicks * Format.SampleRate / Stopwatch.Frequency;
            long dueFrames = targetFrames - processedFrames;
            if (dueFrames > Format.SampleRate / 10)
            {
                processedFrames = targetFrames - (Format.SampleRate / 10);
                dueFrames = targetFrames - processedFrames;
            }

            while (dueFrames >= blockFrames)
            {
                lock (_gate)
                {
                    // Discard stale PCM after a scheduler stall or clock drift.
                    while (_pcm.BufferedDuration.TotalMilliseconds > 60)
                    {
                        int removed = _pcm.Read(block);
                        Interlocked.Add(ref _droppedBytes, removed);
                    }

                    _pcm.Read(block);
                }

                _encoder.Write(block);
                processedFrames += blockFrames;
                dueFrames -= blockFrames;
            }
        }
    }

    private async Task WatchDevicesAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? missingSince = null;
        using var clock = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await clock.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                using MMDevice current = SelectDevice(_config.AudioDevice);
                if (_capturing && current.ID == _device?.ID && current.State == DeviceState.Active && Volatile.Read(ref _captureError) == null)
                {
                    continue;
                }

                CloseCapture();
                OpenCapture(); // Keep the encoder and URL when the endpoint changes.
                missingSince = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _capturing = false;
                missingSince ??= DateTimeOffset.UtcNow;
                Log.Write($"Audio device unavailable; sending silence while retrying: {ex.Message}");
                if (DateTimeOffset.UtcNow - missingSince > TimeSpan.FromSeconds(30))
                {
                    throw new IOException("Cannot capture audio: device unavailable for 30 seconds.", ex);
                }
            }
        }
    }

    private void CloseCapture()
    {
        _capturing = false;
        if (_recorder != null)
        {
            _recorder.DataAvailable -= Capture;
            _recorder.Dispose();
            _recorder = null;
        }

        _device?.Dispose();
        _device = null;
        lock (_gate)
        {
            _pcm.ClearBuffer();
        }
    }

    public void Dispose()
    {
        _disposing = true;
        CloseCapture();
        _encoder.Dispose();
    }
}
