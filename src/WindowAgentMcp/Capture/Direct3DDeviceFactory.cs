using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WindowAgentMcp.Capture;

internal sealed class Direct3DDeviceFactory : IDisposable
{
    private ID3D11Device? _nativeDevice;
    private ID3D11DeviceContext? _immediateContext;
    private IDirect3DDevice? _winRtDevice;
    private int _disposed;

    public IDirect3DDevice Device => _winRtDevice ??= CreateDevice();

    private IDirect3DDevice CreateDevice()
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        ID3D11Device device;
        ID3D11DeviceContext immediateContext;
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out device,
            out immediateContext);

        if (result.Failure)
        {
            result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out device,
                out immediateContext);
        }

        result.CheckError();
        _nativeDevice = device;
        _immediateContext = immediateContext;

        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        var hresult = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var devicePointer);
        Marshal.ThrowExceptionForHR(hresult);

        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(devicePointer);
        }
        finally
        {
            Marshal.Release(devicePointer);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _winRtDevice = default;
        _immediateContext?.Dispose();
        _nativeDevice?.Dispose();
        _immediateContext = null;
        _nativeDevice = null;
        GC.SuppressFinalize(this);
    }

    [DllImport("d3d11.dll", PreserveSig = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
