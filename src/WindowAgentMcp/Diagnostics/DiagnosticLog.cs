using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WindowAgentMcp.Diagnostics;

internal static class DiagnosticLog
{
    private static readonly object Sync = new();
    private static readonly string FileName = $"window-agent-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}.log";
    private static bool _initialized;

    public static void Write(string eventName, string? details = null, Exception? exception = null)
    {
        try
        {
            var timestamp = DateTimeOffset.Now;
            var logDirectory = GetLogDirectory();
            var path = Path.Combine(logDirectory, FileName);
            var entry = new StringBuilder()
                .Append(timestamp.ToString("O"))
                .Append(" pid=")
                .Append(Environment.ProcessId)
                .Append(" event=")
                .Append(eventName);

            if (!string.IsNullOrWhiteSpace(details))
            {
                entry.Append(" ").Append(details);
            }

            entry.AppendLine();
            if (exception is not null)
            {
                entry.Append("exception=").AppendLine(exception.ToString());
            }

            lock (Sync)
            {
                if (!_initialized)
                {
                    PrepareLogDirectory(logDirectory);
                    _initialized = true;
                }

                File.AppendAllText(path, entry.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception loggingException)
        {
            System.Diagnostics.Trace.WriteLine($"WindowAgentMcp diagnostic logging failed: {loggingException.Message}");
        }
    }

    private static string GetLogDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("WINDOW_AGENT_LOG_DIRECTORY");
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowAgentMcp",
                "logs")
            : Path.GetFullPath(overridePath);
    }

    private static void PrepareLogDirectory(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        var files = Directory.EnumerateFiles(logDirectory, "window-agent-*.log")
            .Select(path => new FileInfo(path))
            .ToArray();

        var cutoff = DateTime.UtcNow.AddDays(-14);
        foreach (var oldFile in files.Where(file => file.LastWriteTimeUtc < cutoff))
        {
            TryDelete(oldFile);
        }

        foreach (var excessFile in files
                     .Where(file => file.Exists)
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Skip(499))
        {
            TryDelete(excessFile);
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class DiagnosticFileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DiagnosticFileLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class DiagnosticFileLogger(string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Information
            && (categoryName == "Microsoft.Hosting.Lifetime"
                || categoryName.StartsWith("ModelContextProtocol.Server.StdioServerTransport", StringComparison.Ordinal));

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            DiagnosticLog.Write(
                "framework",
                $"level={logLevel} category={categoryName} eventId={eventId.Id} message={formatter(state, exception)}",
                exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
