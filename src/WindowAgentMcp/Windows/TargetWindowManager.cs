namespace WindowAgentMcp.Windows;

public sealed class TargetWindowManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private WindowSession? _current;
    private int _disposed;

    public WindowSession? Current => Volatile.Read(ref _current);

    public async Task SelectAsync(WindowDescriptor descriptor, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(descriptor);
        await _selectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        WindowSession? candidate = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            candidate = new WindowSession(descriptor);
            if (!candidate.IsValid())
            {
                throw new InvalidOperationException("The selected window closed or changed before it could be connected. Refresh the list and select it again.");
            }

            await candidate.StartCaptureAsync(cancellationToken).ConfigureAwait(false);
            if (!candidate.IsValid())
            {
                throw new InvalidOperationException("The selected window closed while capture was starting. Refresh the list and select it again.");
            }

            var previous = Interlocked.Exchange(ref _current, null);
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            Volatile.Write(ref _current, candidate);
            candidate = null;
        }
        finally
        {
            if (candidate is not null)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }

            _selectionGate.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _selectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = Interlocked.Exchange(ref _current, null);
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _selectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var previous = Interlocked.Exchange(ref _current, null);
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _selectionGate.Release();
        }
    }
}
