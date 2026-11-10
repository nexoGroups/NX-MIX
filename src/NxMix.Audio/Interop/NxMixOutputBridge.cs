using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NxMix.Audio.Interop;

internal sealed unsafe class NxMixOutputBridge : IDisposable
{
    private static readonly Guid CaptureCategory = new("65e8773d-8f56-11d0-a3b9-00a0c9223196");
    private static readonly Guid BridgeSet = new("42d9fe2e-72d9-42f0-995f-765c970a38f7");
    private const uint KsPropertyControl = 0x002F0003;
    public const int SampleRate = 48000;
    public const int BlockFrames = 480;
    public const int BlockAlign = AudioDeviceInfo.NxMixMaxChannels * sizeof(int);

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    private readonly SafeFileHandle _handle;
    private NxMixOutputBridge(SafeFileHandle handle) => _handle = handle;

    public static NxMixOutputBridge Open(int port)
    {
        if (port is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(port));
        string suffix = "\\NxMixCaptureWave" + port;
        string? path = ListInterfaces().SingleOrDefault(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (path == null) throw new AudioException(0, $"NX-MIX Out {port} wurde im WaveRT-Treiber nicht gefunden.");
        SafeFileHandle handle = CreateFileW(path, 0xC0000000, 3, 0, 3, 0, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new AudioException(0, $"NX-MIX Out {port} konnte nicht geöffnet werden (Win32 {error}).");
        }
        return new NxMixOutputBridge(handle);
    }

    public void Write(byte* frames)
    {
        var property = new KsProperty { Set = BridgeSet, Flags = 2 };
        if (!DeviceIoControl(_handle, KsPropertyControl, &property, (uint)sizeof(KsProperty),
            frames, BlockFrames * BlockAlign, out _, 0))
        {
            int error = Marshal.GetLastWin32Error();
            throw new AudioException(0, $"Der WaveRT-Treiber hat den Audioblock abgelehnt (Win32 {error}).");
        }
    }

    private static string[] ListInterfaces()
    {
        const uint BufferSmall = 0x1A;
        for (int retry = 0; retry < 3; retry++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint length, in CaptureCategory, null, 0) != 0 || length < 2)
                break;
            var buffer = new char[length];
            fixed (char* data = buffer)
            {
                uint result = CM_Get_Device_Interface_ListW(in CaptureCategory, null, data, length, 0);
                if (result == 0) return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (result != BufferSmall) break;
            }
        }
        return [];
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, in Guid category, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(in Guid category, string? deviceId, char* buffer, uint length, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, KsProperty* input, uint inputSize,
        byte* output, uint outputSize, out uint returned, nint overlapped);
}
