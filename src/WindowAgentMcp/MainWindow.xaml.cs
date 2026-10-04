using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowAgentMcp.Diagnostics;
using WindowAgentMcp.Windows;

namespace WindowAgentMcp;

public partial class MainWindow : Window
{
    private readonly TargetWindowManager _targetManager;
    private readonly WindowCatalog _catalog = new();
    private readonly ObservableCollection<WindowDescriptor> _windows = [];
    private readonly DispatcherTimer _targetMonitor;

    public MainWindow(TargetWindowManager targetManager)
    {
        _targetManager = targetManager;
        InitializeComponent();
        WindowList.ItemsSource = _windows;

        _targetMonitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _targetMonitor.Tick += TargetMonitor_Tick;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        RefreshStatus();
        _targetMonitor.Start();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowList.SelectedItem is not WindowDescriptor descriptor)
        {
            return;
        }

        ConnectButton.IsEnabled = false;
        try
        {
            await _targetManager.SelectAsync(descriptor, CancellationToken.None);
            StatusText.Text = $"已连接：{descriptor.Title}（{descriptor.ProcessName}，PID {descriptor.ProcessId}）";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("selector.connect_failed", exception: exception);
            MessageBox.Show(this, exception.Message, "无法连接窗口", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshStatus();
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await _targetManager.ClearAsync();
        RefreshStatus();
    }

    private void WindowList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshStatus();

    private async void TargetMonitor_Tick(object? sender, EventArgs e)
    {
        var current = _targetManager.Current;
        if (current is not null && !current.IsValid())
        {
            await _targetManager.ClearAsync();
            StatusText.Text = "目标窗口已关闭，请重新选择。";
        }

        RefreshStatus();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _targetMonitor.Stop();
        DiagnosticLog.Write("selector.closed");
    }

    private void RefreshWindows()
    {
        try
        {
            var selected = WindowList.SelectedItem as WindowDescriptor;
            _windows.Clear();
            foreach (var descriptor in _catalog.EnumerateVisibleWindows())
            {
                _windows.Add(descriptor);
            }

            if (selected is not null)
            {
                WindowList.SelectedItem = _windows.FirstOrDefault(candidate =>
                    candidate.Handle == selected.Handle
                    && candidate.ProcessId == selected.ProcessId
                    && candidate.ProcessStartUtcTicks == selected.ProcessStartUtcTicks);
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("selector.refresh_failed", exception: exception);
            MessageBox.Show(this, exception.Message, "无法枚举窗口", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_targetManager.Current is { } current)
        {
            var descriptor = current.Descriptor;
            StatusText.Text = $"已连接：{descriptor.Title}（{descriptor.ProcessName}，PID {descriptor.ProcessId}）";
        }
        else if (StatusText.Text.StartsWith("已连接：", StringComparison.Ordinal))
        {
            StatusText.Text = "尚未选择窗口";
        }

        ConnectButton.IsEnabled = WindowList.SelectedItem is WindowDescriptor;
        DisconnectButton.IsEnabled = _targetManager.Current is not null;
    }
}
