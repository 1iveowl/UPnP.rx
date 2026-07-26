# UPnP.Rx 5.0.0 — the honest-optionals release

**Status: proposed, awaiting author sign-off on §7 (Q1-Q5).** Assumes
`SSDP.UPnP.PCL` 9.0.0 has shipped with upstream candidates 7, 8 and 9 addressed
(see [project plan §9](upnp-rx-project-plan.md)). Successor to the 4.2.0
structural batch; consolidates the 4.3 shelf recorded in
[DECISIONS.md](DECISIONS.md).

## 1. Why this is 5.0.0 and not 4.3.0

The shelf was recorded as "4.3", but the work cannot be a minor. `DiscoveredDevice.BootId`
is in the shipped public-API ledger as `-> uint`, and the entire point of this release is
that it must become nullable so "the device sent no BOOTID" stops masquerading as
"the device sent 0". That is source- and binary-breaking for every consumer.

The alternative — keep `uint BootId` and add a parallel nullable accessor beside it — was
considered and rejected: it perpetuates the exact falsehood the release exists to remove,
and the API ledger would carry two ways to ask one question forever. The project already
versions on meaning rather than ceremony (it opened at 3.0.0 to reflect its lineage), so a
major here is in character.

Going major also **unblocks the namespace harmonization** that DECISIONS.md explicitly
gated on "at a major" (§6c). That item rides along free rather than waiting for an
unrelated future break — which is the strongest practical argument for doing all of this
in one release rather than dribbling it out.

## 2. Theme: absence should be representable

Every change below is one idea applied consistently — *an optional field the device did not
send must not be reported as a value it did not send.* This is house leniency rule 4
("unparsable optional fields stay unset") finally applied to the discovery envelope, where
it was quietly violated. It is also, concretely, the fix for a real device on the author's
network: a Platinum renderer that sends no BOOTID at all, and whose reboots are therefore
invisible to the roster today (see [v4.1 plan §7d](upnp-rx-v4.1-plan.md)).

Our own surface mirrors upstream's inconsistency precisely: `DiscoveredDevice.ConfigId` is
already `int?`, and `BootId` is the lone non-nullable holdout — exactly as `CONFIGID` is
nullable upstream while `BOOTID` is not.

## 3. Upstream adoption (SSDP.UPnP.PCL 9.0.0)

Expected 9.0.0 surface changes and every place we touch:

| Upstream change | Our adoption points |
|---|---|
| `Notify.BOOTID` / `MSearchResponse.BOOTID`: `uint` → `uint?` | `UpnpClient` (5 construction sites), `DiscoveredDevice.BootId`, the discovery dedup key, `DescriptionCache` key, `RosterSource` reboot detection |
| new `NLS` (`string?`) | `DiscoveredDevice`, `RosterSource` reboot detection |
| `Date`: `DateTimeOffset` → `DateTimeOffset?` | not currently surfaced; verify no regression |
| `UpnpMajorVersion`/`UpnpMinorVersion` nullable (or `int?`) | the version reconciliation (§5) |
| `IsUpnp2` deprecated or redefined | must not be used anywhere; it is false for UDA 1.1 |
| `ParseMaxAge` fixed (candidate 6) | `Announcement.MaxAge` becomes trustworthy; see §4c |

Adoption is mechanical but wide. Do it as its own phase, green, before any new feature
lands on top of it.

## 4. What changes on our surface

### 4a. The boot signature

`BootId` becomes `uint?`. Alongside it, `Nls` (`string?`) carries the UPnP 1.0 boot
signature that 9.0.0 now parses. Reboot detection compares the pair, not the number:

- `RosterSource` currently does `device.BootId != entry.Device.BootId`, which for a UPnP
  1.0 device is `0 != 0` forever. It becomes a comparison over whichever signature the
  device actually supplies, and detects nothing when the device supplies neither
  (correct: no evidence is not evidence of change).
- `DescriptionCache` keys on `location#configId#bootId`; the boot component becomes the
  signature, so a UPnP 1.0 device that reboots finally re-reads its description.
- The discovery dedup key (`UpnpClient`, `Distinct(...)`) takes the same treatment.

**Open shape question — Q1 (§7).** Two raw properties, or one `BootSignature` value?

### 4b. `Announcement.MaxAge`

Documented today as "`TimeSpan.Zero` when absent" — the same sentinel smell, and now
fixable. Becomes `TimeSpan?`. `RosterSource`'s `effectiveMaxAge` fallback stays exactly as
it is (it already treats non-positive as unknown); only the type gets honest.

### 4c. Nothing else moves

`ConfigId` is already `int?`. `HasParsingError`, `Usn`, `Location`, `Server`,
`LocalEndPoint` are unchanged. Keep the break surface minimal and explicable in one
release-note sentence.

## 5. UPnP version reconciliation

The spec question is settled and recorded ([v4.1 plan §7e](upnp-rx-v4.1-plan.md)): UDA 2.0
makes both `SERVER`'s `UPnP/x.y` token and the description's `<specVersion>` normative,
calls them "the same information", and names **no authority** between them. A disagreement
is therefore device non-conformance, not format noise.

**Recommendation: put the reconciliation in the library, but not as a scalar.** A single
`UpnpVersion` property would embed an invented precedence rule and discard the very fact
that makes it honest — whether the sources agree. It also cannot live in one place, since
`SERVER` arrives at discovery while `<specVersion>` only exists after describing. Proposed
shape:

```csharp
/// <summary>What a device claims about the UDA version it implements, per source.</summary>
public sealed record UpnpVersionClaims(
    Version? FromServer,        // SERVER's UPnP/x.y token   (known at discovery)
    Version? FromDescription,   // <specVersion>             (known after describe)
    Version? Effective,         // the conservative reconciliation - see below
    bool SourcesAgree);
```

`DiscoveredDevice` exposes the discovery-time claim; `DescribedDevice` exposes the full
record. **Documented precedence: the conservative minimum**, not "prefer one source". The
spec gives no basis for preferring either, so the tie-break has to be argued on
consequences: over-claiming means relying on features the device may not implement
(BOOTID, CONFIGID, SEARCHPORT, `ssdp:update`) — precisely the failure this release exists
to fix — while under-claiming costs only an unused capability.

That makes `Effective` load-bearing rather than decorative, and it interlocks with §4a: if
`Effective` is below 1.1, an absent BOOTID is **expected**, not a defect; at 1.1 or above,
an absent BOOTID *is* a conformance defect worth surfacing. The two themes are one release
for a reason.

Note there are up to four witnesses (device `SERVER`, device `<specVersion>`, per-service
SCPD `<specVersion>`, and `SERVER` on control/eventing responses). **Q2 (§7)** asks how
many to model.

## 6. Carried shelf items

- **6a. Local-IPv4 helper promotion.** The interface enumeration exists privately in
  `PortMapper` and again in the dashboard's `NetworkClientProvider`. Unifying needs new
  public API, which is why it waited. Ships here.
- **6b. `[LoggerMessage]` source-generated logging.** Non-breaking, removes the
  `IsEnabled` guard boilerplate added in the dedup pass, and cuts allocation on the hot
  announcement path. Classes become `partial`.
- **6c. Namespace harmonization.** `Announcement`, `DiscoveredDevice`, `IUpnpClient` live
  in `UPnP.Rx`; `RosterChange` and friends live in `UPnP.Rx.Roster`. Recommendation: bring
  the roster types to the root namespace — the roster is a first-class client feature
  reached through `IUpnpClient.Roster()`, not a subsystem, and consumers already import
  `UPnP.Rx` for everything adjacent. **Q3 (§7).**

## 7. Open questions for the author

- **Q1 — boot signature shape.** (a) Two raw properties, `uint? BootId` and `string? Nls`,
  consumer compares both; or (b) a `BootSignature` value type carrying either, with value
  equality, so change detection is one comparison. (a) is closer to the wire and adds no
  vocabulary; (b) makes the roster code obviously correct and cannot be misused by
  comparing only half the identity. *Leaning (b), because the failure mode of (a) is a
  silent one.*
- **Q2 — how many version witnesses to model.** Device `SERVER` + device `<specVersion>`
  only (simple, covers the badge), or also per-service SCPD `<specVersion>` and
  control-response `SERVER`? *Leaning the first two now, with the record shaped so the
  others can be added without breaking.*
- **Q3 — namespace direction** (§6c): roster types to `UPnP.Rx`, or `Announcement` and
  friends into a `UPnP.Rx.Presence`? *Leaning the former.*
- **Q4 — is 5.0.0 agreed?** (§1). If a major is unwanted this release cannot proceed as
  designed; the fallback is shipping only the non-breaking parts (6a, 6b, the badge fed by
  a locally-parsed version) and deferring honest optionals indefinitely.
- **Q5 — dashboard scope.** Version badges plus the BOOTID-label guard are in. Should the
  disagreement badge be visible by default, or behind the existing expand affordance?

## 8. Build phases (one commit each, green at every commit)

| Phase | Work |
|---|---|
| P1 | Adopt SSDP.UPnP.PCL 9.0.0 mechanically; no behavior change beyond types (§3) |
| P2 | Boot signature: `BootId`/`Nls`, roster reboot detection, cache key, dedup key (§4a) |
| P3 | `Announcement.MaxAge` → `TimeSpan?` (§4b) |
| P4 | `UpnpVersionClaims` + parsing + precedence, on both device types (§5) |
| P5 | Namespace harmonization (§6c) — pure move, mechanical, isolated for reviewability |
| P6 | Local-IPv4 helper promotion (§6a); `[LoggerMessage]` conversion (§6b) |
| P7 | Dashboard: version badges with provenance, disagreement flag, BOOTID label guard |
| P8 | API ledger roll (`PublicAPI.Unshipped.txt` removals + additions → `Shipped`), README, CODEMAP, release |

## 9. Testing

- Real-device fixtures for the new parsing, including the captured Platinum response
  (no BOOTID, `01-NLS` present, `SERVER: UPnP/1.0, DLNADOC/1.50 Platinum/1.0.5.13`) —
  that one string exercises absent-BOOTID, NLS-present, and the version misparse at once.
- Roster: a UPnP 1.0 device whose NLS changes must produce `DeviceUpdated` and invalidate
  its cached description. This test fails on 4.2.0 by construction — it is the release's
  reason for existing.
- Version reconciliation: agreeing sources; disagreeing sources; each source absent;
  neither present. Assert `Effective` is the minimum and `SourcesAgree` is accurate.
- Soak tests must stay green unchanged — nothing here alters lifetime or boundedness.

## 10. Risks

- **The break is wide but shallow.** `uint` → `uint?` touches every consumer that reads
  `BootId`, but each fix is a `?? 0` or a null check. Release notes must show the
  before/after in one snippet.
- **Upstream shape may differ from the prompts.** If 9.0.0 lands `NLS` or the version
  fields differently than proposed, P1 absorbs the difference and P2/P4 adapt; do not
  design against the prompt text, design against the shipped assembly.
- **`Effective` is our invention.** It must be documented as an engineering tie-break with
  the spec's silence stated plainly, never as a spec requirement.

## 11. Out of scope

Raw SSDP wire log (upstream, `SimpleHttpListener.Rx`); multicast eventing; the Q7 upstream
filings (dotnet/reactive WASM scheduler, ReactiveUI `WithBlazorWasm` default, SSDP
candidates 1/2), which remain independent of this release.
