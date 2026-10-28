using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using Raumfeld.DesktopStream.Audio;
using Raumfeld.DesktopStream.Raumfeld;
using Raumfeld.DesktopStream.Runtime;

namespace Raumfeld.DesktopStream.Diagnostics;

public sealed record CorrelationPeak(int Sample, double Confidence);
internal sealed record AcousticProbe(CorrelationPeak Reference, CorrelationPeak Microphone, double MicrophoneRms);

public static class LatencyMeasurement
{
    public const int SampleRate = 8000;

    public static float[] CreateChirp(int sampleRate)
    {
        float[] samples = new float[sampleRate * 35 / 100];
        double duration = samples.Length / (double)sampleRate;
        for (int index = 0; index < samples.Length; index++)
        {
            double time = index / (double)sampleRate;
            double phase = 2 * Math.PI * ((350 * time) + (0.5 * (2200 - 350) / duration * time * time));
            samples[index] = (float)(0.07 * Math.Sin(phase) * FastFourierTransform.HannWindow(index, samples.Length));
        }

        return samples;
    }

    public static CorrelationPeak FindPeak(float[] recording, float[] template, int firstSample, int lastSample,
        IReadOnlyList<int>? excludedSamples = null)
    {
        int length = 1;
        int exponent = 0;
        while (length < recording.Length + template.Length)
        {
            length *= 2;
            exponent++;
        }

        var signal = new Complex[length];
        var pattern = new Complex[length];
        double[] energy = new double[recording.Length + 1];
        double templateEnergy = 0;
        for (int index = 0; index < recording.Length; index++)
        {
            signal[index].X = recording[index];
            energy[index + 1] = energy[index] + (recording[index] * recording[index]);
        }

        for (int index = 0; index < template.Length; index++)
        {
            pattern[template.Length - index - 1].X = template[index];
            templateEnergy += template[index] * template[index];
        }

        FastFourierTransform.FFT(true, exponent, signal);
        FastFourierTransform.FFT(true, exponent, pattern);
        for (int index = 0; index < length; index++)
        {
            float real = (signal[index].X * pattern[index].X) - (signal[index].Y * pattern[index].Y);
            signal[index].Y = (signal[index].X * pattern[index].Y) + (signal[index].Y * pattern[index].X);
            signal[index].X = real;
        }

        FastFourierTransform.FFT(false, exponent, signal);
        var best = new CorrelationPeak(0, 0);
        int end = Math.Min(lastSample, recording.Length - template.Length);
        for (int index = Math.Max(0, firstSample); index <= end; index++)
        {
            if (excludedSamples?.Any(excluded => Math.Abs(index - excluded) < SampleRate * 15 / 100) == true)
            {
                continue;
            }

            double windowEnergy = energy[index + template.Length] - energy[index];
            if (windowEnergy < 1e-10 || templateEnergy == 0)
            {
                continue;
            }

            double confidence = Math.Abs(signal[index + template.Length - 1].X * length)
                / Math.Sqrt(windowEnergy * templateEnergy);
            if (confidence > best.Confidence)
            {
                best = new CorrelationPeak(index, Math.Min(1, confidence));
            }
        }

        return best;
    }

    public static async Task<int> RunAsync(Configuration config, CancellationToken cancellationToken)
    {
        SessionStatus? session = await InstanceControl.RequestAsync("status");
        if (session?.State != "Streaming" || !session.CaptureActive)
        {
            throw new InvalidOperationException("Start a streaming session before measuring latency.");
        }

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        Uri host = await Network.DiscoverAsync(config, http, cancellationToken);
        var client = new RaumfeldClient(http, host);
        Room room = Protocol.ResolveRoom(await client.GetRoomsAsync(cancellationToken), session.Room);
        if (room.ZoneUdn == null)
        {
            throw new IOException("The active room no longer has a zone.");
        }

        string rendererUdn = room.ZoneUdn;
        Renderer transport = await client.ResolveRendererAsync(rendererUdn, cancellationToken)
            ?? throw new IOException("The active zone renderer disappeared.");
        Renderer rendering = await client.ResolveRendererAsync(rendererUdn, cancellationToken, "RenderingControl")
            ?? throw new IOException("No rendering control is available for the leakage check.");
        if (!(await client.GetStateAsync(transport, cancellationToken)).Owns(new Uri(session.StreamUrl!)))
        {
            throw new IOException("The receiver changed source before measurement.");
        }

        bool originalMute = await client.GetMuteAsync(rendering, cancellationToken);
        if (originalMute)
        {
            throw new IOException("The Raumfeld zone is muted. Unmute it before measuring.");
        }

        AcousticProbe local;
        try
        {
            await client.SetMuteAsync(rendering, true, cancellationToken);
            Console.WriteLine("Local leakage check. Raumfeld is muted briefly.");
            local = await CaptureProbeAsync(session.Capture ?? config.AudioDevice, 5, [], cancellationToken);
        }
        finally
        {
            using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await client.SetMuteAsync(rendering, originalMute, restore.Token);
            }
            catch (Exception exception)
            {
                throw new IOException("Could not restore the Raumfeld mute state. Unmute the zone manually.", exception);
            }
        }

        Console.WriteLine($"Local check loopback correlation: {local.Reference.Confidence:F3}; microphone: {local.Microphone.Confidence:F3}");
        if (local.Reference.Confidence < 0.65)
        {
            throw new IOException("No reliable Windows loopback reference was recorded.");
        }

        int[] excluded = local.Microphone.Confidence >= 0.12
            ? [local.Microphone.Sample - local.Reference.Sample]
            : [];
        Console.WriteLine($"Local leakage: {(local.Microphone.Sample - local.Reference.Sample) * 1000.0 / SampleRate:F1} ms; correlation {local.Microphone.Confidence:F3}");
        if (!(await client.GetStateAsync(transport, cancellationToken)).Owns(new Uri(session.StreamUrl!)))
        {
            throw new IOException("The receiver changed source during measurement.");
        }

        Console.WriteLine("Raumfeld playback restored. Measuring the speaker path.");
        AcousticProbe heard = await CaptureProbeAsync(session.Capture ?? config.AudioDevice, 15, excluded, cancellationToken);
        Console.WriteLine($"Loopback correlation: {heard.Reference.Confidence:F3}; speaker correlation: {heard.Microphone.Confidence:F3}");
        Console.WriteLine($"Microphone RMS: {heard.MicrophoneRms:F6}");
        if (heard.Reference.Confidence < 0.65 || heard.Microphone.Confidence < 0.20)
        {
            Console.WriteLine("No reliable speaker match after excluding local leakage. Move the microphone closer to the Stereo M.");
            return 1;
        }

        double milliseconds = (heard.Microphone.Sample - heard.Reference.Sample) * 1000.0 / SampleRate;
        Console.WriteLine($"Acoustic speaker delay: {milliseconds:F1} ms. Includes room and microphone capture.");
        return 0;
    }

    private static async Task<AcousticProbe> CaptureProbeAsync(string outputDevice, int durationSeconds,
        IReadOnlyList<int> excludedLags, CancellationToken cancellationToken)
    {
        long epoch = 0;
        float[] reference = new float[SampleRate * durationSeconds];
        float[] microphone = new float[reference.Length];
        float[] chirp = CreateChirp(SampleRate);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice output = AudioPipeline.SelectDevice(outputDevice);
        using MMDevice input = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        Console.WriteLine($"Microphone measurement only: {input.FriendlyName}");
        Console.WriteLine("A short chirp will play through Windows and Raumfeld. No recording is saved.");
        using WasapiRecorder loopback = new WasapiRecorderBuilder().WithDevice(output)
            .WithLoopbackCapture().WithFormat(format).WithBufferLength(20).Build();
        using WasapiRecorder capture = new WasapiRecorderBuilder().WithDevice(input)
            .WithFormat(format).WithBufferLength(20).Build();
        Exception? captureError = null;
        var loopbackStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var microphoneStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loopback.DataAvailable += (bytes, flags, position, qpc) => CopyCapture(bytes, flags, qpc, epoch, reference);
        capture.DataAvailable += (bytes, flags, position, qpc) => CopyCapture(bytes, flags, qpc, epoch, microphone);
        capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception != null)
            {
                Interlocked.CompareExchange(ref captureError, args.Exception, null);
            }
            microphoneStopped.TrySetResult();
        };
        loopback.RecordingStopped += (_, args) =>
        {
            if (args.Exception != null)
            {
                Interlocked.CompareExchange(ref captureError, args.Exception, null);
            }
            loopbackStopped.TrySetResult();
        };

        float[] playback = new float[SampleRate * durationSeconds];
        chirp.CopyTo(playback, SampleRate);
        byte[] playbackBytes = MemoryMarshal.AsBytes(playback.AsSpan()).ToArray();
        using var source = new RawSourceWaveStream(playbackBytes, 0, playbackBytes.Length, format);
        await using WasapiPlayer player = new WasapiPlayerBuilder().WithDevice(output).WithLatency(30).Build();
        try
        {
            player.Init(source);
            epoch = (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
            loopback.StartRecording();
            capture.StartRecording();
            player.Play();
            await Task.Delay(TimeSpan.FromSeconds(durationSeconds - 1), cancellationToken);
            player.Stop();
            loopback.StopRecording();
            capture.StopRecording();
            await Task.WhenAll(loopbackStopped.Task, microphoneStopped.Task).WaitAsync(cancellationToken);
            if (captureError != null)
            {
                throw new IOException("Latency recording failed.", captureError);
            }

            CorrelationPeak sent = FindPeak(reference, chirp, 0, reference.Length);
            int[] excluded = [.. excludedLags.Select(lag => sent.Sample + lag)];
            CorrelationPeak heard = FindPeak(microphone, chirp, sent.Sample + (SampleRate / 30), microphone.Length, excluded);
            return new AcousticProbe(sent, heard,
                Math.Sqrt(microphone.Sum(value => (double)value * value) / microphone.Length));
        }
        finally
        {
            player.Stop();
            loopback.Dispose();
            capture.Dispose();
            Array.Clear(reference);
            Array.Clear(microphone);
            Array.Clear(playback);
            Array.Clear(playbackBytes);
        }
    }

    private static void CopyCapture(ReadOnlySpan<byte> bytes, AudioClientBufferFlags flags, long qpc, long epoch, float[] target)
    {
        if ((flags & AudioClientBufferFlags.TimestampError) != 0)
        {
            return;
        }

        int offset = (int)((qpc - epoch) * SampleRate / 10_000_000);
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(bytes);
        if (offset < 0)
        {
            int skip = Math.Min(samples.Length, -offset);
            samples = samples[skip..];
            offset = 0;
        }

        if (offset < target.Length)
        {
            samples[..Math.Min(samples.Length, target.Length - offset)].CopyTo(target.AsSpan(offset));
        }
    }
}
