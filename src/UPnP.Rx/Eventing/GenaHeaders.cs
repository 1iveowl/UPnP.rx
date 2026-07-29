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
    /// <param name="timeout">The duration to compose; <see langword="null"/> for <c>Second-infinite</c>.</param>
    /// <remarks>
    /// At least one whole second, because that is what the header can carry. Truncating
    /// instead would compose <c>Second-0</c> for a sub-second duration, and a negative one
    /// would compose <c>Second--5</c> - neither is a GENA header, and both used to go out
    /// without complaint. This is the "strict in what we send" half of this class's contract,
    /// and it now matches <see cref="ParseTimeout"/>, which has always refused a non-positive
    /// value on the way in.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is under one second.</exception>
    public static string ComposeTimeout(TimeSpan? timeout)
    {
        if (timeout is not { } t)
        {
            return "Second-infinite";
        }

        if (t < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout), t, "A GENA subscription timeout must be at least one second.");
        }

        return $"Second-{(long)t.TotalSeconds}";
    }

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
