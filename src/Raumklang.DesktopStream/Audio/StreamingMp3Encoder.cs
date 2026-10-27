using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Raumklang.DesktopStream.Audio;

public sealed class StreamingMp3Encoder : IDisposable
{
    private readonly SafeLameHandle _lame;
    private readonly Stream _output;
    private readonly float[] _samples;
    private readonly byte[] _encoded = new byte[16_384];
    private readonly WaveFormat _format;
    private long _inputFrames;
    private bool _disposed;

    public double FirstOutputInputMilliseconds { get; private set; }

    public StreamingMp3Encoder(Stream output, WaveFormat format, int bitrateKbps)
    {
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32
            || format.Channels != 2 || format.SampleRate is not (44100 or 48000))
        {
            throw new ArgumentException("MP3 input must be 44.1 or 48 kHz stereo float PCM.", nameof(format));
        }

        _output = output;
        _format = format;
        _samples = new float[format.SampleRate / 100 * format.Channels];
        _lame = LameNative.Create();
        try
        {
            if (_lame.IsInvalid)
            {
                throw new IOException("LAME could not allocate an encoder.");
            }

            Check(LameNative.SetInputRate(_lame, format.SampleRate));
            Check(LameNative.SetOutputRate(_lame, format.SampleRate));
            Check(LameNative.SetChannels(_lame, format.Channels));
            Check(LameNative.SetBitrate(_lame, bitrateKbps));
            Check(LameNative.SetVbrTag(_lame, 0));
            // Independent frames let a client join without waiting for the bit reservoir.
            Check(LameNative.SetDisableReservoir(_lame, 1));
            Check(LameNative.SetQuality(_lame, 2));
            Check(LameNative.Initialize(_lame));
        }
        catch
        {
            _lame.Dispose();
            throw;
        }
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pcm.Length % _format.BlockAlign != 0)
        {
            throw new ArgumentException("PCM must contain complete stereo frames.", nameof(pcm));
        }

        ReadOnlySpan<float> input = MemoryMarshal.Cast<byte, float>(pcm);
        while (!input.IsEmpty)
        {
            int count = Math.Min(input.Length, _samples.Length);
            input[..count].CopyTo(_samples);
            _inputFrames += count / _format.Channels;
            int encoded = LameNative.Encode(_lame, _samples, count / _format.Channels, _encoded, _encoded.Length);
            if (encoded < 0)
            {
                throw new IOException($"LAME encoding failed with code {encoded}.");
            }

            if (encoded > 0)
            {
                if (FirstOutputInputMilliseconds == 0)
                {
                    FirstOutputInputMilliseconds = _inputFrames * 1000.0 / _format.SampleRate;
                }

                _output.Write(_encoded, 0, encoded);
            }

            input = input[count..];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            int count = LameNative.Flush(_lame, _encoded, _encoded.Length);
            if (count > 0)
            {
                _output.Write(_encoded, 0, count);
            }
        }
        finally
        {
            _lame.Dispose();
        }
    }

    private static void Check(int result)
    {
        if (result < 0)
        {
            throw new IOException($"LAME configuration failed with code {result}.");
        }
    }
}
