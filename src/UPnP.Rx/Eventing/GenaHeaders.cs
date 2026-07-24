namespace UPnP.Rx.Eventing;

/// <summary>
/// Pure helpers for GENA's header vocabulary (UDA 2.0 clause 4): the
/// <c>TIMEOUT: Second-n</c> format, the <c>CALLBACK</c> URL form and the
/// <c>SEQ</c> event key. Strict in what we send, lenient in what we accept.
/// </summary>
public static class GenaHeaders
{
    /// <summary>
    /// The wire form of a requested/granted subscription duration:
    /// <c>Second-1800</c>, or <c>Second-infinite</c> when null.
    /// </summary>
    public static string ComposeTimeout(TimeSpan? timeout) =>
        timeout is { } t ? $"Second-{(long)t.TotalSeconds}" : "Second-infinite";

    /// <summary>
    /// Parses a <c>TIMEOUT</c> header value. Lenient: casing, stray whitespace
    /// and bare numbers are accepted; <c>infinite</c>, absent or garbage yield
    /// <see langword="null"/> (callers fall back to their own default).
    /// </summary>
    public static TimeSpan? ParseTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var token = value.Trim();

        if (token.StartsWith("Second-", StringComparison.OrdinalIgnoreCase))
        {
            token = token["Second-".Length..].Trim();
        }

        return long.TryParse(token, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    /// <summary>The wire form of the callback URL: <c>&lt;http://host:port/path&gt;</c>.</summary>
    public static string ComposeCallback(Uri callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return $"<{callback}>";
    }

    /// <summary>
    /// Parses a <c>SEQ</c> event key; <see langword="null"/> when absent or not
    /// a number (some devices omit it - the engine then skips gap detection).
    /// </summary>
    public static uint? ParseSeq(string? value) =>
        uint.TryParse(value?.Trim(), out var seq) ? seq : null;
}
