using System.Reflection;
using UPnP.Rx.PortMapping;
using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>
/// Keeps <see cref="TestKit.PortMappingStub"/> honest against the real library.
/// </summary>
/// <remarks>
/// The rules are tested against a source stub, because a test compilation is built on
/// reference assemblies that top out at .NET 9 and cannot reference a net10.0 library at
/// all. That is a real hazard: rename <c>lease</c> to <c>duration</c> in the shipped API
/// and every UPNPRX001 test keeps passing against a stub that still says <c>lease</c>,
/// while the rule silently stops firing for anyone.
///
/// So the stub's load-bearing shapes are asserted against the real types by reflection.
/// This project references UPnP.Rx directly (which the test COMPILATIONS cannot), which
/// is what makes that possible.
/// </remarks>
public class StubGuardTests
{
    [Fact]
    public void TheLeaseParameterIsStillCalledLease()
    {
        // UPNPRX001 finds the argument by parameter name rather than position, so this
        // name is part of the rule's contract with the library.
        Assert.All(
            new[] { nameof(IInternetGateway.AddPortMappingAsync), nameof(IInternetGateway.AddAnyPortMappingAsync) },
            name => Assert.Contains(
                typeof(IInternetGateway).GetMethod(name)!.GetParameters(),
                p => p.Name == "lease" && p.ParameterType == typeof(TimeSpan)));
    }

    [Fact]
    public void ThePortMapperOneLinerStillTakesALease()
    {
        var method = typeof(PortMapper)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(PortMapper.AddPortMappingAsync));

        Assert.Contains(method.GetParameters(), p => p.Name == "lease" && p.ParameterType == typeof(TimeSpan));
    }

    [Fact]
    public void TheEntryPropertyIsStillCalledLeaseDuration_AndStillNullable()
    {
        var property = typeof(PortMappingEntry).GetProperty(nameof(PortMappingEntry.LeaseDuration));

        Assert.NotNull(property);
        Assert.Equal(typeof(TimeSpan?), property.PropertyType);
    }

    [Fact]
    public void TheRangeConstantsStillMatchWhatTheRuleHardCodes()
    {
        // The rule carries 0-604800 as literals, because a netstandard2.0 analyzer cannot
        // reference the net10.0 library that declares them. This is the join between the
        // two copies: if the library's maximum ever moves, the rule is wrong and this says so.
        Assert.Equal(604_800, (int)LeaseDurations.Maximum.TotalSeconds);
        Assert.Equal(TimeSpan.Zero, LeaseDurations.Indefinite);
    }

    [Fact]
    public void ThePortMappingNamespaceIsStillWhatTheRuleMatchesOn()
    {
        // The rule identifies our types by containing namespace. A namespace harmonisation
        // like 5.0.0's (Roster -> UPnP.Rx.Presence) would silently disarm it.
        Assert.Equal("UPnP.Rx.PortMapping", typeof(IInternetGateway).Namespace);
        Assert.Equal("UPnP.Rx.PortMapping", typeof(PortMappingEntry).Namespace);
        Assert.Equal("UPnP.Rx.PortMapping", typeof(PortMapper).Namespace);
    }
}
