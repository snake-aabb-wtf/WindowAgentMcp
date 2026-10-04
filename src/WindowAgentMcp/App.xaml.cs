using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WindowAgentMcp.Diagnostics;
using WindowAgentMcp.Mcp;
using WindowAgentMcp.Windows;

namespace WindowAgentMcp;

public partial class App : Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private McpServerHost? _mcpHost;
    private TargetWindowManager? _targetWindowManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        DiagnosticLog.Write(
            "app.starting",
            $"os={Environment.OSVersion.Version} runtime={Environment.Version}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            DiagnosticLog.Write("app.unsupported_os");
            MessageBox.Show(
                "WindowAgentMcp 需要 Windows 10 版本 1903（内部版本 18362）或更高版本。",
                "Windows 版本不受支持",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var targetWindowManager = new TargetWindowManager();
        _targetWindowManager = targetWindowManager;
        var window = new MainWindow(targetWindowManager);
        MainWindow = window;
        DiagnosticLog.Write("selector.created", "visibility=hidden");

        _mcpHost = new McpServerHost(services =>
        {
            services.AddSingleton(targetWindowManager);
            services.AddSingleton(new WindowCatalog());
            services.AddSingleton(new WindowPickerPresenter(window));
        });

        _ = RunMcpServerAsync(_mcpHost);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticLog.Write("app.exiting", $"exitCode={e.ApplicationExitCode}");
        _lifetime.Cancel();
        try
        {
            _targetWindowManager?.ClearAsync().GetAwaiter().GetResult();
            _mcpHost?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _mcpHost?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("app.exit_cleanup_failed", exception: exception);
            System.Diagnostics.Trace.TraceError("MCP shutdown failed: {0}", exception.Message);
        }
        finally
        {
            _targetWindowManager?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _lifetime.Dispose();
            DiagnosticLog.Write("app.exit_cleanup_completed");
        }

        base.OnExit(e);
    }

    private async Task RunMcpServerAsync(McpServerHost host)
    {
        DiagnosticLog.Write("mcp.server_task.started");
        try
        {
            await Task.Run(() => host.RunAsync(_lifetime.Token));
            DiagnosticLog.Write("mcp.server_task.completed");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            DiagnosticLog.Write("mcp.server_task.cancelled", "reason=application_shutdown");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("mcp.server_task.failed", exception: exception);
            await Dispatcher.InvokeAsync(() => MessageBox.Show(
                $"MCP stdio 服务已停止：{exception.Message}",
                "MCP 服务错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error));
        }
        finally
        {
            DiagnosticLog.Write("app.shutdown_requested", "source=mcp_server_task");
            await Dispatcher.InvokeAsync(() => Shutdown());
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        DiagnosticLog.Write("ui.unhandled_exception", exception: e.Exception);

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        DiagnosticLog.Write(
            "appdomain.unhandled_exception",
            $"isTerminating={e.IsTerminating}",
            e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        DiagnosticLog.Write("task.unobserved_exception", exception: e.Exception);
}
