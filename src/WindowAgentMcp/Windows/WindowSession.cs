using System.Diagnostics;
using WindowAgentMcp.Capture;
using WindowAgentMcp.Errors;
using WindowAgentMcp.Input;

namespace WindowAgentMcp.Windows;

public sealed class WindowSession : IAsyncDisposable
{
    private int _disposed;
    private WindowCaptureService? _captureService;
    private ClientAreaInfo? _lastCapturedArea;

    internal WindowSession(WindowDescriptor descriptor)
    {
        Descriptor = descriptor;
        Input = new InputController(this);
    }

    public WindowDescriptor Descriptor { get; }
    public InputController Input { get; }

    public ClientAreaInfo? LastCapturedArea => Volatile.Read(ref _lastCapturedArea);

    public bool IsValid()
    {
        if (Volatile.Read(ref _disposed) != 0 || !NativeMethods.IsWindow(Descriptor.Handle))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(Descriptor.Handle, out var actualProcessId);
        if (actualProcessId != Descriptor.ProcessId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(checked((int)actualProcessId));
            return process.StartTime.ToUniversalTime().Ticks == Descriptor.ProcessStartUtcTicks;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or NotSupportedException
                                           or System.Security.SecurityException
                                           or UnauthorizedAccessException
                                           or OverflowException)
        {
            return false;
        }
    }

    internal async Task StartCaptureAsync(CancellationToken cancellationToken)
    {
        if (!IsValid())
        {
            throw new WindowAgentException("window_closed", "The selected window closed or changed before capture could start.");
        }

        var captureService = new WindowCaptureService(Descriptor.Handle);
        try
        {
            await captureService.StartAsync(cancellationToken).ConfigureAwait(false);
            _captureService = captureService;
        }
        catch
        {
            await captureService.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<CapturedImage> CaptureAsync(CancellationToken cancellationToken)
    {
        if (!IsValid())
        {
            throw new WindowAgentException("window_closed", "The selected window is no longer available. Select a window again.");
        }

        if (NativeMethods.IsIconic(Descriptor.Handle))
        {
            throw new WindowAgentException("window_minimized", "Restore the selected window before capturing it.");
        }

        if (_captureService is null)
        {
            throw new WindowAgentException("capture_unavailable", "The selected window capture session is not active.");
        }

        var image = await _captureService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (!IsValid())
        {
            throw new WindowAgentException("window_closed", "The selected window closed during capture.");
        }

        var area = GetClientAreaInfo();
        if (image.Width != area.Width || image.Height != area.Height || image.Dpi != area.Dpi)
        {
            throw new WindowAgentException("capture_unavailable", "The window changed size or DPI during capture. Take another screenshot.");
        }

        Volatile.Write(ref _lastCapturedArea, area);
        return image with { TargetTitle = Descriptor.Title };
    }

    public ClientAreaInfo GetClientAreaInfo()
    {
        if (!IsValid())
        {
            throw new WindowAgentException("window_closed", "The selected window is no longer available. Select a window again.");
        }

        if (!NativeMethods.GetClientRect(Descriptor.Handle, out var rect))
        {
            throw new WindowAgentException("window_closed", "Unable to read the selected window's client area.");
        }

        var origin = default(NativeMethods.NativePoint);
        if (!NativeMethods.ClientToScreen(Descriptor.Handle, ref origin))
        {
            throw new WindowAgentException("window_closed", "Unable to map the selected window's client area to screen coordinates.");
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var dpi = NativeMethods.GetDpiForWindow(Descriptor.Handle);
        if (width <= 0 || height <= 0 || dpi == 0)
        {
            throw new WindowAgentException("window_minimized", "The selected window has no active client area.");
        }

        return new ClientAreaInfo(width, height, origin.X, origin.Y, dpi);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await Input.DisposeAsync().ConfigureAwait(false);
        var captureService = Interlocked.Exchange(ref _captureService, null);
        if (captureService is not null)
        {
            await captureService.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed record ClientAreaInfo(int Width, int Height, int ScreenX, int ScreenY, uint Dpi);
