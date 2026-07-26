using Xunit;

namespace UPnP.Rx.Tests;

/// <summary>
/// The boot identity's whole job is telling three states apart: a device that
/// announced a BOOTID, one that announced only the UPnP 1.0 NLS, and one that
/// announced neither. Collapsing any pair of those is the bug this type replaced.
/// </summary>
public class BootSignatureTests
{
    [Fact]
    public void None_IsNotKnown()
    {
        Assert.False(BootSignature.None.IsKnown);
        Assert.False(default(BootSignature).IsKnown);
    }

    [Fact]
    public void ZeroBootId_IsKnown_AndDistinctFromAbsent()
    {
        // 0 is a legal BOOTID (UDA 2.0 clause 1.2.2 ranges the field 0..2^31-1),
        // so it must not be confused with "the device sent none".
        var zero = new BootSignature(0, null);

        Assert.True(zero.IsKnown);
        Assert.NotEqual(BootSignature.None, zero);
    }

    [Fact]
    public void NlsOnly_IsKnown()
    {
        Assert.True(new BootSignature(null, "1785066224").IsKnown);
        Assert.False(new BootSignature(null, "").IsKnown);
    }

    [Theory]
    [InlineData(1u, null, 2u, null, true)]          // BOOTID advanced
    [InlineData(1u, null, 1u, null, false)]         // same boot instance
    [InlineData(null, "a", null, "b", true)]        // UPnP 1.0 device restarted
    [InlineData(null, "a", null, "a", false)]       // same signature
    public void IndicatesRebootSince_ComparesWhateverTheDeviceSupplied(
        uint? previousBootId, string? previousNls, uint? currentBootId, string? currentNls, bool expected)
    {
        var previous = new BootSignature(previousBootId, previousNls);
        var current = new BootSignature(currentBootId, currentNls);

        Assert.Equal(expected, current.IndicatesRebootSince(previous));
    }

    [Fact]
    public void IndicatesRebootSince_IsFalseWhenEitherSideIsUnknown()
    {
        // No evidence is not evidence of change - otherwise every announcement
        // from a device without a boot identity would look like a reboot.
        Assert.False(BootSignature.None.IndicatesRebootSince(BootSignature.None));
        Assert.False(new BootSignature(1, null).IndicatesRebootSince(BootSignature.None));
        Assert.False(BootSignature.None.IndicatesRebootSince(new BootSignature(1, null)));
    }

    [Fact]
    public void ToString_IsStableAndDistinguishesTheThreeStates()
    {
        Assert.Equal("7", new BootSignature(7, null).ToString());
        Assert.Equal("nls:abc", new BootSignature(null, "abc").ToString());
        Assert.Equal("-", BootSignature.None.ToString());
    }
}
