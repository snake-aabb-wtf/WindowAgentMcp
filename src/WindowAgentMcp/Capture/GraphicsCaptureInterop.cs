using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace WindowAgentMcp.Capture;

internal static class GraphicsCaptureInterop
{
    private const string CaptureItemRuntimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    private static readonly Guid InteropInterfaceId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid CaptureItemInterfaceId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static GraphicsCaptureItem CreateForWindow(nint hwnd)
    {
        var hresult = WindowsCreateString(CaptureItemRuntimeClass, (uint)CaptureItemRuntimeClass.Length, out var className);
        Marshal.ThrowExceptionForHR(hresult);

        try
        {
            var interopId = InteropInterfaceId;
            hresult = RoGetActivationFactory(className, ref interopId, out var factory);
            Marshal.ThrowExceptionForHR(hresult);

            try
            {
                var captureItemId = CaptureItemInterfaceId;
                hresult = factory.CreateForWindow(hwnd, ref captureItemId, out var itemPointer);
                Marshal.ThrowExceptionForHR(hresult);

                try
                {
                    return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
                }
                finally
                {
                    Marshal.Release(itemPointer);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        finally
        {
            _ = WindowsDeleteString(className);
        }
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        nint activatableClassId,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IGraphicsCaptureItemInterop factory);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint hwnd, ref Guid iid, out nint result);

        [PreserveSig]
        int CreateForMonitor(nint monitor, ref Guid iid, out nint result);
    }
}
