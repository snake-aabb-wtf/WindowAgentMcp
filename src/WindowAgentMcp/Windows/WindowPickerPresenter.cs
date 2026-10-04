using System.Windows;
using System.Windows.Threading;
using WindowAgentMcp.Diagnostics;

namespace WindowAgentMcp.Windows;

public sealed class WindowPickerPresenter(Window window)
{
    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticLog.Write("selector.requested", "source=mcp_tool_call");
        await window.Dispatcher.InvokeAsync(() =>
        {
            if (!window.IsVisible)
            {
                window.Show();
                DiagnosticLog.Write("selector.shown", "source=mcp_tool_call");
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        }, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
    }
}
