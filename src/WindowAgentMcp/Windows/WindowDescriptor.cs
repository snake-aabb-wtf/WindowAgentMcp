namespace WindowAgentMcp.Windows;

public sealed record WindowDescriptor(
    nint Handle,
    uint ProcessId,
    long ProcessStartUtcTicks,
    string Title,
    string ProcessName,
    string ClassName,
    bool IsMinimized)
{
    public string DisplayName => IsMinimized
        ? $"{Title} — {ProcessName}（PID {ProcessId}，已最小化）"
        : $"{Title} — {ProcessName}（PID {ProcessId}）";
}
