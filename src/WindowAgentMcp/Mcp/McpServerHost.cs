using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindowAgentMcp.Diagnostics;
using ModelContextProtocol.Server;

namespace WindowAgentMcp.Mcp;

public sealed class McpServerHost : IAsyncDisposable
{
    private readonly IHost _host;
    private int _started;

    public McpServerHost(Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.AddProvider(new DiagnosticFileLoggerProvider());

        configureServices?.Invoke(builder.Services);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<WindowAutomationTools>();

        _host = builder.Build();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The MCP server host can only be run once.");
        }

        await _host.StartAsync(cancellationToken).ConfigureAwait(false);
        DiagnosticLog.Write("mcp.host.started");
        await _host.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        DiagnosticLog.Write("mcp.host.stopped", "reason=host_shutdown_signal");
    }

    public Task StopAsync(CancellationToken cancellationToken) => _host.StopAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        return ValueTask.CompletedTask;
    }
}
