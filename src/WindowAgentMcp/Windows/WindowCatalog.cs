using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowAgentMcp.Windows;

public sealed class WindowCatalog
{
    private const int TextCapacity = 2048;

    public IReadOnlyList<WindowDescriptor> EnumerateVisibleWindows()
    {
        var windows = new List<WindowDescriptor>();
        var ownProcessId = (uint)Environment.ProcessId;

        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0 || processId == ownProcessId || IsCloaked(hwnd))
            {
                return true;
            }

            var titleBuffer = new StringBuilder(TextCapacity);
            if (NativeMethods.GetWindowText(hwnd, titleBuffer, titleBuffer.Capacity) <= 0)
            {
                return true;
            }

            var title = titleBuffer.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            var classBuffer = new StringBuilder(512);
            _ = NativeMethods.GetClassName(hwnd, classBuffer, classBuffer.Capacity);

            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                var startTicks = process.StartTime.ToUniversalTime().Ticks;
                windows.Add(new WindowDescriptor(
                    hwnd,
                    processId,
                    startTicks,
                    title,
                    process.ProcessName,
                    classBuffer.ToString(),
                    NativeMethods.IsIconic(hwnd)));
            }
            catch (Exception exception) when (exception is ArgumentException
                                               or InvalidOperationException
                                               or Win32Exception
                                               or NotSupportedException
                                               or System.Security.SecurityException
                                               or UnauthorizedAccessException
                                               or OverflowException)
            {
                // Without a readable PID and start time, the window cannot be safely rebound later.
            }

            return true;
        };

        if (!NativeMethods.EnumWindows(callback, nint.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(error, "Unable to enumerate top-level windows.");
            }
        }

        return windows
            .OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool IsCloaked(nint hwnd)
    {
        var cloaked = 0;
        return NativeMethods.DwmGetWindowAttribute(
                   hwnd,
                   NativeMethods.DwmwaCloaked,
                   out cloaked,
                   sizeof(int)) == 0
               && cloaked != 0;
    }
}
