# UDA 2.0 clause 4 (GENA eventing) compliance review - 4.0.0, E5 (2026-07-24)

Reviewed against the UDA 2.0 text (`pdftotext` extraction; line refs into it). Scope:
control-point/subscriber obligations as implemented by `GenaHeaders`, `GenaParser`,
`EventCallbackListener`, `HttpGenaTransport`, `GenaSubscriptionSource`.
**Awaiting author sign-off** - finding 2 changed code; the rest record verified compliance
or deliberate leniency.

| # | Requirement (spec ref) | Verdict |
|---|---|---|
| 1 | **SUBSCRIBE request shape (4.1.2)**: `CALLBACK: <url>`, `NT: upnp:event`, `TIMEOUT: Second-n`; renewal carries `SID` (and no CALLBACK/NT); UNSUBSCRIBE carries `SID`. | ✅ Compliant - `HttpGenaTransport` sends exactly these; `GenaHeaders` composes the wire forms. |
| 2 | **NOTIFY error handling (4.3.3 error table, l. ~6505-6525)**: missing NT/NTS → 400; NT ≠ `upnp:event`, NTS ≠ `upnp:propchange`, or missing/unknown SID → 412. | 🔧 **Fixed in E5** - the listener previously checked only its route token; it now enforces the full table (case-insensitively, per house leniency). Tests added. |
| 3 | **Acknowledge within 30 s (l. 6472)**: the subscriber shall respond to NOTIFY within 30 seconds. | ✅ Compliant - the handler is synchronous parse+emit; the 200 goes out immediately after. |
| 4 | **SEQ semantics (4.3.2/4.3.4)**: 0 = initial state set; monotonically increasing; wraps back to 1 (not 0) after 4294967295. | ✅ Compliant with a note - gap detection treats any unexpected value as a gap and resubscribes (decision Q2), which also covers the wrap case conservatively (a wrap in practice requires ~4.3 billion events on one subscription). Devices that omit SEQ skip gap detection (leniency). |
| 5 | **TIMEOUT parsing (4.1.1/4.1.2)**: `Second-n` responses; devices may grant different values than requested; `infinite` is legacy. | ✅ Compliant - lenient parse (casing, bare numbers); `infinite`/garbage → null → the engine falls back to the requested duration, so it still renews (harmless extra renewals on a truly infinite grant). |
| 6 | **Renewal before expiry (4.1.2)**: subscribers should renew before TIMEOUT lapses. | ✅ Compliant - renewal at half of the *granted* timeout on the options' TimeProvider. |
| 7 | **UNSUBSCRIBE on exit (4.1.3)**: subscribers should unsubscribe when no longer interested. | ✅ Compliant - runs in the engine task's teardown on last Rx disposal; abrupt process death leaves the device to expire the subscription at TIMEOUT (the finite-lease property, documented). |
| 8 | **propertyset body (4.3.5)**: `e:propertyset/e:property/<variableName>` in the event namespace. | ✅ Compliant on send-side-only obligations n/a; parsing is namespace/case-tolerant per the leniency policy, empty sets accepted as keep-alives. |
| 9 | **Initial event message (4.3.1)**: the device sends the full evented state with SEQ 0 after accepting a subscription; it may arrive before the SUBSCRIBE response. | ✅ Compliant - per-attempt route tokens make early NOTIFYs routable before the SID is known (the Sonos race, absorbed by construction). |
| 10 | **Multicast eventing (4.3.3 §multicast)**: optional device feature over UDP. | ⛔ Out of scope for 4.0 (unicast eventing only), recorded here deliberately. |

## Notes for the author

- Finding 2 is the only behavior change; it makes us a *stricter* NOTIFY receiver in exactly
  the way the spec demands, while staying lenient about casing.
- The 400/412 responses carry no body; the spec requires none for NOTIFY errors.
- Real-hardware validation is the §5 guide in the eventing plan - findings 4/5/9 all have
  device-quirk surface that only your network can exercise.
