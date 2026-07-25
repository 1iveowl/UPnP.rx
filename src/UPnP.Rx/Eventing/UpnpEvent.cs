namespace UPnP.Rx.Eventing;

/// <summary>
/// One event from a GENA subscription's stream (<c>UpnpService.Events()</c>).
/// A small closed union: state changes are data, and so are the subscription's
/// lifecycle moments - per-item failure never terminates the stream
/// (Rx rule 6; recovery policy per plan decision Q2).
/// </summary>
public abstract record UpnpEvent
{
    private protected UpnpEvent()
    {
    }
}

/// <summary>An evented state variable changed (or was reported in an initial/replayed state set).</summary>
/// <param name="Name">The state variable's name.</param>
/// <param name="Value">The raw value; escaped payloads (e.g. AVTransport <c>LastChange</c>) arrive decoded but untyped.</param>
/// <param name="Seq">The event key of the NOTIFY that carried it (<c>SEQ</c>; 0 is the initial state set).</param>
/// <param name="IsInitialState">True when this belongs to a subscription's initial full-state NOTIFY (SEQ 0).</param>
/// <param name="IsReplay">True when this is the engine replaying last-known state to a late subscriber (plan decision Q5).</param>
public sealed record PropertyChange(
    string Name, string Value, uint Seq, bool IsInitialState, bool IsReplay) : UpnpEvent;

/// <summary>The subscription is live on the device.</summary>
/// <param name="Sid">The subscription identifier the device assigned.</param>
/// <param name="Timeout">The granted subscription duration (renewed automatically at half-life).</param>
public sealed record Subscribed(string Sid, TimeSpan Timeout) : UpnpEvent;

/// <summary>A renewal attempt failed; the engine keeps retrying / resubscribing (decision Q2).</summary>
/// <param name="Message">What went wrong.</param>
public sealed record RenewalFailed(string Message) : UpnpEvent;

/// <summary>A fresh SUBSCRIBE succeeded after a failure or SEQ gap; a new initial state set follows.</summary>
/// <param name="Sid">The new subscription identifier.</param>
public sealed record Resubscribed(string Sid) : UpnpEvent;

/// <summary>
/// NOTIFYs arrived out of sequence - events may have been lost. The engine
/// resubscribes (fresh full state) when auto-recovery is on.
/// </summary>
/// <param name="ExpectedSeq">The event key that was expected next.</param>
/// <param name="ActualSeq">The event key that actually arrived.</param>
public sealed record GapDetected(uint ExpectedSeq, uint ActualSeq) : UpnpEvent;

/// <summary>
/// The device refused the initial SUBSCRIBE in a way that cannot succeed on
/// retry: HTTP 405/501 (the endpoint does not implement eventing, despite the
/// service advertising an <c>eventSubURL</c>) or 404/410 (the advertised
/// <c>eventSubURL</c> does not exist on the device - a placeholder like
/// Sonos's <c>/ssdp/notfound</c>). This is the stream's last event before it
/// terminates with <c>OnError</c>; auto-resubscribe does not apply, because
/// the refusal contradicts the device's own description and only a
/// re-announced (re-described) device can change it.
/// </summary>
/// <param name="HttpStatus">The refusing HTTP status code (404, 405, 410 or 501).</param>
/// <param name="Reason">Why the engine gives up instead of retrying.</param>
public sealed record SubscriptionRefused(int HttpStatus, string Reason) : UpnpEvent;

/// <summary>One variable from a parsed NOTIFY property set. Immutable.</summary>
/// <param name="Name">The state variable's name.</param>
/// <param name="Value">The value as carried (entity-decoded by XML parsing; otherwise verbatim).</param>
public sealed record EventedProperty(string Name, string Value);
