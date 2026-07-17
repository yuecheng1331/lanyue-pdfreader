namespace LocalPdfReader.App;

/// <summary>
/// Coalesces rapid view changes before they are persisted.  It deliberately owns
/// no document data, keeping persistence policy outside the WPF view model.
/// </summary>
internal sealed class ReadingStateCoordinator : IDisposable
{
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _pendingSaveCancellationSource;

    public ReadingStateCoordinator(TimeSpan delay)
    {
        _delay = delay;
    }

    public void Schedule(Func<CancellationToken, Task> saveAsync)
    {
        ArgumentNullException.ThrowIfNull(saveAsync);

        Cancel();
        _pendingSaveCancellationSource = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(saveAsync, _pendingSaveCancellationSource.Token);
    }

    public void Cancel()
    {
        _pendingSaveCancellationSource?.Cancel();
        _pendingSaveCancellationSource?.Dispose();
        _pendingSaveCancellationSource = null;
    }

    public void Dispose() => Cancel();

    private async Task SaveAfterDelayAsync(
        Func<CancellationToken, Task> saveAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_delay, cancellationToken);
            await saveAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
