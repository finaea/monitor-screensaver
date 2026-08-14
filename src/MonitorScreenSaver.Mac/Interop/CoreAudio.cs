using System.Runtime.InteropServices;

namespace MonitorScreenSaver.Mac.Interop;

internal static class CoreAudio
{
    private const string Lib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    // FourCC selectors verified against AudioHardware.h / AudioHardwareBase.h in the
    // macOS 26 SDK: 'dev#', 'dOut', 'gone', 'glob', 'outp', 'stm#'.
    internal const uint kAudioObjectSystemObject = 1;
    internal static readonly uint kAudioHardwarePropertyDevices = FourCC("dev#");
    internal static readonly uint kAudioDevicePropertyDeviceIsRunningSomewhere = FourCC("gone");
    internal static readonly uint kAudioDevicePropertyStreams = FourCC("stm#");
    internal static readonly uint kAudioObjectPropertyScopeGlobal = FourCC("glob");
    internal static readonly uint kAudioObjectPropertyScopeOutput = FourCC("outp");
    internal const uint kAudioObjectPropertyElementMain = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioObjectPropertyAddress
    {
        public uint Selector, Scope, Element;

        public AudioObjectPropertyAddress(uint selector, uint scope)
        {
            Selector = selector;
            Scope = scope;
            Element = kAudioObjectPropertyElementMain;
        }
    }

    [DllImport(Lib)]
    internal static extern int AudioObjectGetPropertyDataSize(
        uint objectID, ref AudioObjectPropertyAddress address, uint qualifierSize, IntPtr qualifier, out uint dataSize);

    [DllImport(Lib)]
    internal static extern int AudioObjectGetPropertyData(
        uint objectID, ref AudioObjectPropertyAddress address, uint qualifierSize, IntPtr qualifier,
        ref uint dataSize, uint[] data);

    [DllImport(Lib)]
    internal static extern int AudioObjectGetPropertyData(
        uint objectID, ref AudioObjectPropertyAddress address, uint qualifierSize, IntPtr qualifier,
        ref uint dataSize, out uint data);

    private static uint FourCC(string code) =>
        ((uint)code[0] << 24) | ((uint)code[1] << 16) | ((uint)code[2] << 8) | code[3];
}
