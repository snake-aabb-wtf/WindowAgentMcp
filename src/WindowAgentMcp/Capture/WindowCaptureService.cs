using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WindowAgentMcp.Errors;
using WindowAgentMcp.Windows;
using WinRT;

namespace WindowAgentMcp.Capture;

public sealed class WindowCaptureService : IAsyncDisposable
{
    private const int FrameCount = 3;
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private readonly nint _hwnd;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly SemaphoreSlim _frameArrived = new(0, 1);
    private Direct3DDeviceFactory? _deviceFactory;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _captureSession;
    private int _sizeChanged;
    private int _closed;
    private int _started;
    private int _disposed;

    public WindowCaptureService(nint hwnd)
    {
        _hwnd = hwnd;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The capture session has already been started.");
        }

        try
        {
            if (NativeMethods.IsIconic(_hwnd))
            {
                throw new WindowAgentException("window_minimized", "Restore the selected window before connecting it.");
            }

            if (!GraphicsCaptureSession.IsSupported())
            {
                throw new WindowAgentException("capture_unavailable", "Windows Graphics Capture is not available on this device.");
            }

            _deviceFactory = new Direct3DDeviceFactory();
            _item = GraphicsCaptureInterop.CreateForWindow(_hwnd);
            _item.Closed += Item_Closed;

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _deviceFactory.Device,
                PixelFormat,
                FrameCount,
                _item.Size);
            _framePool.FrameArrived += FramePool_FrameArrived;
            _captureSession = _framePool.CreateCaptureSession(_item);
            _captureSession.StartCapture();
        }
        catch (WindowAgentException)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new WindowAgentException("capture_unavailable", "Unable to start capture for the selected window.", exception);
        }
    }

    public async Task<CapturedImage> CaptureAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            using var frame = await TakeFreshFrameAsync(cancellationToken).ConfigureAwait(false);
            if (!NativeMethods.IsWindow(_hwnd))
            {
                throw new WindowAgentException("window_closed", "The selected window closed during capture.");
            }

            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            var geometry = GetClientCrop(_hwnd, bitmap.PixelWidth, bitmap.PixelHeight);

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(bitmap);
            // BitmapTransform scales the source before applying Bounds. Normalize the
            // captured frame to physical screen pixels first, then crop in that space.
            encoder.BitmapTransform.ScaledWidth = checked((uint)geometry.ScaledWidth);
            encoder.BitmapTransform.ScaledHeight = checked((uint)geometry.ScaledHeight);
            encoder.BitmapTransform.Bounds = geometry.SourceBounds;
            await encoder.FlushAsync();

            var size = checked((uint)stream.Size);
            stream.Seek(0);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var loaded = await reader.LoadAsync(size);
            if (loaded != size)
            {
                throw new WindowAgentException("capture_unavailable", "The PNG encoder returned an incomplete image.");
            }

            var pngBytes = new byte[checked((int)loaded)];
            reader.ReadBytes(pngBytes);
            return new CapturedImage(pngBytes, geometry.ClientWidth, geometry.ClientHeight, "client-px", geometry.Dpi, string.Empty);
        }
        catch (WindowAgentException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new WindowAgentException("capture_unavailable", "Unable to capture the selected window.", exception);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async Task<Direct3D11CaptureFrame> TakeFreshFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ThrowIfUnavailable();
            if (Interlocked.Exchange(ref _sizeChanged, 0) != 0)
            {
                RecreateFramePool();
            }

            if (!await _frameArrived.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
            {
                throw new WindowAgentException("capture_unavailable", "No fresh frame arrived from the selected window within five seconds.");
            }

            ThrowIfUnavailable();
            if (Interlocked.Exchange(ref _sizeChanged, 0) != 0)
            {
                RecreateFramePool();
                continue;
            }

            var frame = _framePool!.TryGetNextFrame();
            if (frame is null)
            {
                continue;
            }

            Direct3D11CaptureFrame latest = frame;
            while (true)
            {
                var next = _framePool.TryGetNextFrame();
                if (next is null)
                {
                    break;
                }

                latest.Dispose();
                latest = next;
            }

            var currentSize = _item!.Size;
            if (latest.ContentSize.Width != currentSize.Width || latest.ContentSize.Height != currentSize.Height)
            {
                latest.Dispose();
                Interlocked.Exchange(ref _sizeChanged, 1);
                RecreateFramePool();
                continue;
            }

            return latest;
        }
    }

    private void RecreateFramePool()
    {
        if (_item is null || _framePool is null || _deviceFactory is null)
        {
            throw new WindowAgentException("capture_unavailable", "The capture frame pool is unavailable.");
        }

        var size = _item.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new WindowAgentException("window_minimized", "Restore the selected window before capturing it.");
        }

        _framePool.Recreate(_deviceFactory.Device, PixelFormat, FrameCount, size);
        while (_frameArrived.Wait(0))
        {
        }
    }

    private static ClientCrop GetClientCrop(nint hwnd, int frameWidth, int frameHeight)
    {
        var clientOrigin = default(NativeMethods.NativePoint);
        if (!NativeMethods.GetClientRect(hwnd, out var clientRect)
            || !NativeMethods.ClientToScreen(hwnd, ref clientOrigin))
        {
            throw new WindowAgentException("capture_unavailable", "Unable to read the selected window's client geometry.");
        }

        var clientWidth = clientRect.Right - clientRect.Left;
        var clientHeight = clientRect.Bottom - clientRect.Top;
        if (clientWidth <= 0 || clientHeight <= 0)
        {
            throw new WindowAgentException("window_minimized", "Restore the selected window before capturing it.");
        }

        var candidates = new List<NativeMethods.NativeRect>(2);
        var extendedBounds = default(NativeMethods.NativeRect);
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DwmwaExtendedFrameBounds,
                out extendedBounds,
                Marshal.SizeOf<NativeMethods.NativeRect>()) == 0)
        {
            candidates.Add(extendedBounds);
        }

        if (NativeMethods.GetWindowRect(hwnd, out var windowBounds))
        {
            candidates.Add(windowBounds);
        }

        var bounds = candidates
            .Where(rect => rect.Right > rect.Left && rect.Bottom > rect.Top)
            .OrderBy(rect => GeometryScore(rect, frameWidth, frameHeight))
            .FirstOrDefault();
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            throw new WindowAgentException("capture_unavailable", "Unable to determine the captured window bounds.");
        }

        var score = GeometryScore(bounds, frameWidth, frameHeight);
        if (!double.IsFinite(score) || score > 0.15)
        {
            throw new WindowAgentException("capture_unavailable", "The capture frame does not match the selected window's physical bounds.");
        }

        var physicalWidth = (long)bounds.Right - bounds.Left;
        var physicalHeight = (long)bounds.Bottom - bounds.Top;
        var x = (long)clientOrigin.X - bounds.Left;
        var y = (long)clientOrigin.Y - bounds.Top;
        if (physicalWidth > int.MaxValue
            || physicalHeight > int.MaxValue
            || x < 0
            || y < 0
            || x + clientWidth > physicalWidth
            || y + clientHeight > physicalHeight)
        {
            throw new WindowAgentException("capture_unavailable", "The selected client area falls outside the captured frame.");
        }

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            throw new WindowAgentException("capture_unavailable", "Unable to read the selected window's DPI.");
        }

        return new ClientCrop(
            new BitmapBounds { X = (uint)x, Y = (uint)y, Width = (uint)clientWidth, Height = (uint)clientHeight },
            (int)physicalWidth,
            (int)physicalHeight,
            clientWidth,
            clientHeight,
            dpi);
    }

    private static double GeometryScore(NativeMethods.NativeRect bounds, int frameWidth, int frameHeight)
    {
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0)
        {
            return double.PositiveInfinity;
        }

        return Math.Abs((double)frameWidth / width - 1.0)
               + Math.Abs((double)frameHeight / height - 1.0);
    }

    private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        SignalFrame();
    }

    private void Item_Closed(GraphicsCaptureItem sender, object args)
    {
        Interlocked.Exchange(ref _closed, 1);
        SignalFrame();
    }

    private void SignalFrame()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            if (_frameArrived.CurrentCount == 0)
            {
                _frameArrived.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _started) == 0)
        {
            throw new WindowAgentException("capture_unavailable", "The capture session is not active.");
        }

        if (Volatile.Read(ref _closed) != 0 || !NativeMethods.IsWindow(_hwnd))
        {
            throw new WindowAgentException("window_closed", "The selected window is no longer available. Select a window again.");
        }

        if (NativeMethods.IsIconic(_hwnd))
        {
            throw new WindowAgentException("window_minimized", "Restore the selected window before capturing it.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_frameArrived.CurrentCount == 0)
            {
                _frameArrived.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }

        await _captureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_item is not null)
            {
                _item.Closed -= Item_Closed;
            }

            if (_framePool is not null)
            {
                _framePool.FrameArrived -= FramePool_FrameArrived;
            }

            _captureSession?.Dispose();
            _framePool?.Dispose();
            _deviceFactory?.Dispose();
            _captureSession = null;
            _framePool = null;
            _item = null;
            _deviceFactory = null;
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private struct ClientCrop
    {
        public ClientCrop(BitmapBounds sourceBounds, int scaledWidth, int scaledHeight, int clientWidth, int clientHeight, uint dpi)
        {
            SourceBounds = sourceBounds;
            ScaledWidth = scaledWidth;
            ScaledHeight = scaledHeight;
            ClientWidth = clientWidth;
            ClientHeight = clientHeight;
            Dpi = dpi;
        }

        public BitmapBounds SourceBounds { get; }
        public int ScaledWidth { get; }
        public int ScaledHeight { get; }
        public int ClientWidth { get; }
        public int ClientHeight { get; }
        public uint Dpi { get; }
    }
}
