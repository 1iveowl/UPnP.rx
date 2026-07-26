namespace UPnP.Rx;

/// <summary>
/// The house shape of a timed HTTP exchange, extracted from its four copies
/// (dedup review, 4.1.1): a timeout on the one clock linked with the caller's
/// token and the client lifetime, and the standard exception vocabulary -
/// lifetime cancellation becomes <see cref="ObjectDisposedException"/>, the
/// timeout becomes a <see cref="UpnpException"/> with the operation's own
/// message, transport failure wraps with the operation's prefix, and the
/// caller's own cancellation passes through untouched.
/// </summary>
internal static class TimedExchange
{
    internal static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> exchange,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken lifetime,
        CancellationToken ct,
        string timeoutMessage,
        string failurePrefix,
        string? disposedMessage = null)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token, lifetime);

        try
        {
            return await exchange(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ObjectDisposedException(
                nameof(UpnpClient), disposedMessage ?? "The owning UpnpClient was disposed.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new UpnpException(timeoutMessage);
        }
        catch (HttpRequestException e)
        {
            throw new UpnpException($"{failurePrefix}: {e.Message}", e);
        }
    }
}
