using Xunit;

namespace UPnP.Rx.Tests.TestHelpers;

/// <summary>
/// The suite's shared vocabulary, deduplicated from per-file copies (dedup
/// review, 4.1.1): fixture loading, and the two fake-clock-safe async waits.
/// </summary>
internal static class TestKit
{
    /// <summary>A real-device XML fixture by file name.</summary>
    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>Yields until the condition holds; asserts if it never does. No real or fake time involved.</summary>
    public static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200_000 && !condition(); i++)
        {
            await Task.Yield();
        }

        Assert.True(condition(), "The condition was not reached.");
    }

    /// <summary>
    /// Drains pending async continuations without real or fake time - for
    /// asserting that nothing further arrives. (A fake-clock Task.Delay would
    /// never elapse here; that mistake once hung the whole run.)
    /// </summary>
    public static async Task SettleAsync()
    {
        for (var i = 0; i < 5_000; i++)
        {
            await Task.Yield();
        }
    }
}
