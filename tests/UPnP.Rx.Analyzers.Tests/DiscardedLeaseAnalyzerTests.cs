using Xunit;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>
/// UPNPRX003. The silence cases matter more here than for the range rules: this one asks
/// "did the value go anywhere", and the honest answer for anything that flows onward is
/// "somebody else owns it now".
/// </summary>
public class DiscardedLeaseAnalyzerTests
{
    private static Task VerifyAsync(string body) =>
        TestKit.VerifyAsync<DiscardedLeaseAnalyzer>(TestKit.InMethod(body), TestKit.PortMappingStub);

    // ---- Reported ----

    [Fact]
    public Task AwaitedAsAStatement_IsReported() => VerifyAsync("""
                await {|UPNPRX003:gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1))|};
        """);

    [Fact]
    public Task AssignedToADiscard_IsReported() => VerifyAsync("""
                _ = await {|UPNPRX003:gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1))|};
        """);

    [Fact]
    public Task AddAnyPortMapping_IsCoveredToo() => VerifyAsync("""
                await {|UPNPRX003:gateway.AddAnyPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1))|};
        """);

    [Fact]
    public Task ThePortMapperOneLiner_IsCovered() => TestKit.VerifyAsync<DiscardedLeaseAnalyzer>("""
        using System;
        using System.Threading.Tasks;
        using UPnP.Rx.PortMapping;

        class Consumer
        {
            // The worst case: this overload's lease owns the discovery client too, so
            // dropping it leaks a UpnpClient and its sockets along with the mapping.
            async Task Run()
            {
                await {|UPNPRX003:PortMapper.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1))|};
            }
        }
        """, TestKit.PortMappingStub);

    // ---- Silence: the value went somewhere ----

    [Fact]
    public Task HeldInAnAwaitUsing_IsSilent() => VerifyAsync("""
                await using var lease = await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1));
        """);

    [Fact]
    public Task HeldInAPlainVariable_IsSilent() => VerifyAsync("""
                // Not this rule's business: the value has a name, so disposing it is a
                // question about the rest of the method rather than about this call.
                var lease = await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1));
                await lease.DisposeAsync();
        """);

    [Fact]
    public Task HeldInASyncUsing_IsSilent() => VerifyAsync("""
                // 'using' rather than 'await using' is a weaker choice - it stops renewing
                // and lets the mapping lapse instead of deleting it - but it is a documented,
                // deliberate one, and telling those apart needs the lease argument from
                // another call site. Out of budget, so out of scope.
                using var lease = await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1));
        """);

    [Fact]
    public Task Returned_IsSilent() => TestKit.VerifyAsync<DiscardedLeaseAnalyzer>("""
        using System;
        using System.Threading.Tasks;
        using UPnP.Rx.PortMapping;

        class Consumer
        {
            // Handing ownership to the caller.
            Task<IPortMappingLease> Run(IInternetGateway gateway) =>
                gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1));
        }
        """, TestKit.PortMappingStub);

    [Fact]
    public Task StoredInAField_IsSilent() => TestKit.VerifyAsync<DiscardedLeaseAnalyzer>("""
        using System;
        using System.Threading.Tasks;
        using UPnP.Rx.PortMapping;

        class Consumer
        {
            private IPortMappingLease? _lease;

            async Task Run(IInternetGateway gateway) =>
                _lease = await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1));
        }
        """, TestKit.PortMappingStub);

    [Fact]
    public Task PassedOnward_IsSilent() => TestKit.VerifyAsync<DiscardedLeaseAnalyzer>("""
        using System;
        using System.Threading.Tasks;
        using UPnP.Rx.PortMapping;

        class Consumer
        {
            static void Track(IPortMappingLease lease) { }

            async Task Run(IInternetGateway gateway) =>
                Track(await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1)));
        }
        """, TestKit.PortMappingStub);

    [Fact]
    public Task ASameNamedMethodInAnotherNamespace_IsSilent() => TestKit.VerifyAsync<DiscardedLeaseAnalyzer>("""
        using System;
        using System.Threading.Tasks;
        using SomeoneElse.PortMapping;

        class Consumer
        {
            async Task Run(IInternetGateway gateway) =>
                await gateway.AddPortMappingAsync(80, 80, Protocol.Tcp, "x", TimeSpan.FromHours(1));
        }
        """, TestKit.LookalikeStub);
}
