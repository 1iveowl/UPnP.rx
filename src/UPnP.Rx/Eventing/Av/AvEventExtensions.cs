using System.Reactive.Linq;

namespace UPnP.Rx.Eventing.Av;

/// <summary>Sugar over <c>UpnpService.Events()</c> for the AV services' <c>LastChange</c> eventing model.</summary>
public static class AvEventExtensions
{
    /// <summary>
    /// Flattens <see cref="PropertyChange"/> events named <c>LastChange</c>
    /// into their decoded per-instance variable changes. Unparsable payloads
    /// contribute nothing (leniency); other events pass through untouched into
    /// nothing - subscribe to the source stream directly when lifecycle events
    /// or the replay/initial flags matter.
    /// </summary>
    /// <param name="events">A service's event stream (typically <c>service.Events()</c>).</param>
    public static IObservable<AvPropertyChange> SelectAvChanges(this IObservable<UpnpEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        return events
            .OfType<PropertyChange>()
            .Where(change => string.Equals(change.Name, "LastChange", StringComparison.OrdinalIgnoreCase))
            .SelectMany(change => LastChangeParser.Parse(change.Value).Match(
                changes => changes,
                _ => []));
    }
}
