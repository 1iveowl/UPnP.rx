using Xunit;

namespace UPnP.Rx.Tests;

/// <summary>
/// UDA 2.0 states the architecture version in several normative places and names no
/// authority between them, so these assert that claims are kept apart rather than
/// collapsed, and that the tie-break is the conservative one.
/// </summary>
public class UpnpVersionClaimsTests
{
    private static UpnpVersionClaim Claim(UpnpVersionSource source, int major, int minor) =>
        new(source, new Version(major, minor));

    [Fact]
    public void None_HasNoEffectiveVersion_AndVacuouslyAgrees()
    {
        Assert.Null(UpnpVersionClaims.None.Effective);
        Assert.True(UpnpVersionClaims.None.SourcesAgree);
        Assert.Empty(UpnpVersionClaims.None.Claims);
    }

    [Fact]
    public void SingleClaim_IsEffective_AndAgrees()
    {
        var claims = UpnpVersionClaims.None.With(Claim(UpnpVersionSource.Server, 1, 0));

        Assert.Equal(new Version(1, 0), claims.Effective);
        Assert.True(claims.SourcesAgree);
    }

    [Fact]
    public void AgreeingSources_Agree()
    {
        var claims = UpnpVersionClaims.None.With(
            Claim(UpnpVersionSource.Server, 2, 0),
            Claim(UpnpVersionSource.DeviceDescription, 2, 0));

        Assert.True(claims.SourcesAgree);
        Assert.Equal(new Version(2, 0), claims.Effective);
    }

    [Fact]
    public void DisagreeingSources_KeepBothClaims_AndTakeTheLower()
    {
        // A device claiming UPnP/2.0 in SERVER but <specVersion>1.0 violates one of
        // two "shall" clauses. Neither reading is discarded, and the conservative
        // one wins, because acting on 2.0 would mean relying on features the 1.0
        // half of the device may not implement.
        var claims = UpnpVersionClaims.None.With(
            Claim(UpnpVersionSource.Server, 2, 0),
            Claim(UpnpVersionSource.DeviceDescription, 1, 0));

        Assert.False(claims.SourcesAgree);
        Assert.Equal(new Version(1, 0), claims.Effective);
        Assert.Equal(2, claims.Claims.Count);
        Assert.Contains(claims.Claims, c => c.Source == UpnpVersionSource.Server && c.Version == new Version(2, 0));
        Assert.Contains(claims.Claims, c => c.Source == UpnpVersionSource.DeviceDescription);
    }

    [Fact]
    public void AllFourWitnesses_Compose()
    {
        var claims = UpnpVersionClaims.None
            .With(Claim(UpnpVersionSource.Server, 1, 1))
            .With(Claim(UpnpVersionSource.DeviceDescription, 1, 1))
            .With(new UpnpVersionClaim(UpnpVersionSource.ServiceDescription, new Version(1, 0), "urn:upnp-org:serviceId:AVTransport"))
            .With(new UpnpVersionClaim(UpnpVersionSource.ControlResponse, new Version(1, 1), "GetVolume"));

        Assert.Equal(4, claims.Claims.Count);
        Assert.False(claims.SourcesAgree);              // one service lags its device
        Assert.Equal(new Version(1, 0), claims.Effective);
        Assert.Equal(
            "urn:upnp-org:serviceId:AVTransport",
            claims.Claims.Single(c => c.Source == UpnpVersionSource.ServiceDescription).Detail);
    }

    [Fact]
    public void With_IsPure()
    {
        var original = UpnpVersionClaims.None.With(Claim(UpnpVersionSource.Server, 1, 0));
        var extended = original.With(Claim(UpnpVersionSource.DeviceDescription, 2, 0));

        Assert.Single(original.Claims);
        Assert.Equal(2, extended.Claims.Count);
    }

    [Fact]
    public void Merging_EmptyClaims_ChangesNothing()
    {
        var claims = UpnpVersionClaims.None.With(Claim(UpnpVersionSource.Server, 1, 0));

        Assert.Same(claims, claims.With(UpnpVersionClaims.None));
        Assert.Same(claims, claims.With());
    }
}
