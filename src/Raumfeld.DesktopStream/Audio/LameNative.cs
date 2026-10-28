using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Raumfeld.DesktopStream.Audio;

internal static class LameNative
{
    private const string Library = "libmp3lame.64.dll";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_init")]
    internal static extern SafeLameHandle Create();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_set_in_samplerate")]
    internal static extern int SetInputRate(SafeLameHandle handle, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_set_out_samplerate")]
    internal static extern int SetOutputRate(SafeLameHandle handle, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_set_num_channels")]
    internal static extern int SetChannels(SafeLameHandle handle, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_set_brate")]
    internal static extern int SetBitrate(SafeLameHandle handle, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_set_bWriteVbrTag")]
    internal static extern int SetVbrTag(SafeLameHandle handle, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_set_disable_reservoir")]
    internal static extern int SetDisableReservoir(SafeLameHandle handle, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_set_quality")]
    internal static extern int SetQuality(SafeLameHandle handle, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_init_params")]
    internal static extern int Initialize(SafeLameHandle handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_encode_buffer_interleaved_ieee_float")]
    internal static extern int Encode(SafeLameHandle handle, [In] float[] samples, int frames, [Out] byte[] output, int length);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_encode_flush")]
    internal static extern int Flush(SafeLameHandle handle, [Out] byte[] output, int length);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_close")]
    internal static extern int Close(IntPtr handle);
}

internal sealed class SafeLameHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeLameHandle() : base(true)
    {
    }

    protected override bool ReleaseHandle()
    {
        return LameNative.Close(handle) == 0;
    }
}
