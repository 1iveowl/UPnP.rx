# UDA 2.0 clause 2/3 compliance review (Phase 5)

Reviewed 2026-07-24 against the UDA 2.0 text (`UPnP-arch-DeviceArchitecture-v2.0.pdf`,
extracted with `pdftotext`; line references are into that extraction). Scope: control-point
obligations of clause 2 (description) and clause 3 (control) as implemented by
`DescriptionParser`, `ScpdParser`, `SoapComposer`, `SoapParser`, `UpnpClient`, `UpnpService`.

**Signed off by the author (Jasper) on 2026-07-24** — findings 2 and 3 changed code; the rest
document verified compliance or accepted deviations.

| # | Requirement (spec ref) | Verdict |
|---|---|---|
| 1 | **URLBase (§2.1, l. 3272–3279):** control points **shall** process `URLBase` when a UPnP 1.0 device ships one, and shall resolve relative URLs per RFC 3986 clause 5 against URLBase-else-location. | ✅ Compliant — and stronger than planned: the plan filed this under "leniency", but it is a mandatory control-point behavior. `Uri.TryCreate(base, rel)` implements RFC 3986 resolution; the Linksys fixture pins it. XML docs updated to cite the clause. |
| 2 | **Control POST `CONTENT-TYPE` (§3.2.1, l. 4979):** field value shall be `text/xml; charset="utf-8"` — charset **quoted**. | 🔧 **Fixed** — `StringContent` emits the unquoted form (`charset=utf-8`); `UpnpService.InvokeAsync` now sets the exact quoted field value. Devices returning `415` on charset mismatches exist. Test added. |
| 3 | **`USER-AGENT` on control (§3.2.1, l. 4981–4985):** allowed; form `OS/version UPnP/2.0 product/version`. | 🔧 **Added** — optional, but identifies the control point per the spec's product-token form: `<platform>/<osversion> UPnP/2.0 UPnP.Rx/<version>`. Test added. |
| 4 | **`SOAPACTION` (§3.2.1, l. 4986–4994):** service type + `#` + action, all in double quotes; version = a version in which the action is defined and which the device supports. | ✅ Compliant — quoted by `SoapComposer.ComposeSoapActionHeader`; the version is the device's own declared `serviceType`, which satisfies both constraints by construction. |
| 5 | **Envelope shape (§3.2.1, l. 5000–5031):** `s:Envelope` in the SOAP namespace with `encodingStyle`; action element qualified by service type, **first child** of `Body`; argument elements unqualified, case-sensitive. | ✅ Compliant — `SoapComposer` emits exactly this shape (round-trip pinned by tests). |
| 6 | **Argument completeness and order (§3.2.1, l. 5029–5031):** every *in* argument shall be included, in SCPD order. | ✅ Compliant for the library's own IGD calls (explicitly ordered, all arguments incl. empty `NewRemoteHost`). For caller-supplied `InvokeAsync` arguments this is the caller's obligation — stated in the XML docs. |
| 7 | **Markup escaping in argument values (§3.2.1, l. 5040–5043):** `&`, `<` etc. shall be escaped per XML 1.0 §2.4. | ✅ Compliant — values are set through `XElement`, escaping is structural (pinned by test). |
| 8 | **Response parsing (§3.2.2):** `[actionName]Response` element; out-arguments in order; device shall respond within 30 s. | ✅ Compliant — parser reads `[action]Response` (with a lenient fallback for misnamed responses); default `ActionTimeout` is exactly the spec's 30 s device budget. |
| 9 | **Fault shape (§3.2.2):** `s:Fault` with `faultcode s:Client`, `faultstring UPnPError`, `detail/UPnPError/errorCode` (401/402/501/6xx). | ✅ Compliant — `SoapParser.ParseFault` reads the `UPnPError` payload (whole-document search tolerates sloppy nesting); `UpnpActionException` carries it. Faults are recognized regardless of HTTP status (leniency; spec mandates 500 but devices vary). |
| 10 | **Ignore unknown elements/attributes/comments/PIs (§2.7, §3.2.1 l. 5033–5039):** control points shall ignore what they do not understand. | ✅ Compliant — parsers read known local names only; everything else is skipped (Livebox vendor-namespace fixture pins it). |
| 11 | **Chunked responses (§2.11, l. 4383–4386):** HTTP/1.1 clients shall support receiving chunked encoding. | ✅ Compliant — `HttpClient`/SocketsHttpHandler native behavior. |
| 12 | **No M-POST (§3, l. 4888):** UDA 2.0 control points use plain POST; M-POST is a UPnP 1.0 legacy. | ✅ Compliant — POST only. |
| 13 | **`configId` (§2, root attribute):** description carries `configId`; changed configuration implies re-fetch. | ✅ Compliant — parsed from the root (namespace/case-tolerant), and the description cache is keyed by LOCATION + CONFIGID, so a config change naturally misses the cache. |
| 14 | **Case sensitivity (§2.5/§3):** XML names are case-sensitive on the wire. | ⚠️ Accepted deviation (receive side only): parsers match names case-insensitively as leniency toward broken devices. Everything we *send* uses exact spec casing. This is intentional (strict-out/lenient-in policy) and cannot cause false negatives on conformant documents. |

## Notes for the author

- Finding 1 upgrades URLBase handling from house policy to cited spec obligation — no code
  change, doc change only.
- Finding 2 is the only behavior change with interop risk in either direction; the quoted form
  is the spec's literal requirement and matches what miniupnpc sends.
- Clause 4 (eventing) was intentionally not reviewed — v2 scope.
- Real-hardware smoke test remains the pre-tag manual step (multicast unavailable here).
