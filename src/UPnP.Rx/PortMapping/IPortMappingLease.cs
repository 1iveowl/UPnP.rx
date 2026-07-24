namespace UPnP.Rx.PortMapping;

/// <summary>
/// An auto-renewing port mapping. Implemented by <see cref="PortMappingLease"/>;
/// exists so consumers can fake a lease in their own tests.
/// </summary>
public interface IPortMappingLease : IAsyncDisposable, IDisposable
{
    /// <summary>The mapping as granted (<c>AddAnyPortMapping</c> may have shifted the external port).</summary>
    PortMappingEntry Mapping { get; }

    /// <summary>Renewal-lifecycle notifications; hot, completes on disposal.</summary>
    IObservable<PortMappingEvent> Events { get; }
}
