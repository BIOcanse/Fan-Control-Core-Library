# Fan Control Core

This repository is the embeddable form of Fan Control Core. It was forked from [Fan-Control-Core](https://github.com/BIOcanse/Fan-Control-Core), which remains the standalone helper program. Here the same fan monitoring and control code is a library, `FanControlCore.Library`, that a host runs inside its own process; [Resource Manager](https://github.com/BIOcanse/Resource-Manager) uses it as an in-process backend. A thin stdio executable is kept, and it speaks the same protocol as the standalone helper: one JSON request and response per line over standard input and output. See "Library hosting and local packages" below for embedding.

## Interface

| Operation | Purpose |
| --- | --- |
| `describe` | List detected fans, the selected backend, and supported controls. |
| `read` | Read current fan state. |
| `set` | Request a duty percentage for a fan when `supportsDuty` is true. |
| `boost` | Request full speed for a fan. |
| `auto` | Return a fan to automatic firmware control. |
| `temp` | Push temperature samples that a software curve follows. |
| `curve` | Read a fan's current curve and how it is executed (firmware table or software plan). |
| `setcurve` | Apply a temperature/percent curve. `execution` chooses firmware or software execution; when omitted the core default applies, and an unavailable choice is rejected. |
| `group` | Switch a control group between firmware and software control in one call (see "Control groups"). |

For example, send `{"id":1,"op":"describe"}` followed by a newline. Replies include the same `id` and an `ok` field. A rejected operation returns `ok:false`, an `error` message and a typed `errorKind` (see "Error kinds"); it does not silently substitute a different fan speed.

Software `setcurve` validates and records or replaces a plan; it starts no execution. Every point must be finite, with a percent from 0 to 100, as for fixed speeds (see "Fixed duty and readings"); otherwise the call is refused as `InvalidRequest` before anything is recorded, rather than failing on the hardware later. `SoftwareCurveEngine.TryRecordPlan` refuses the same points for direct engine users. The embedding host owns `Task RunSoftwareCurveAsync(CancellationToken stop)` on `FanCore` or `FanControlSession`. Call it for the desired execution lifetime and await it after cancellation. Only one run may be active; a concurrent call fails with `InvalidOperationException`. The run waits without ticking when there is no plan it can act on, and otherwise executes every plan at the existing 100 ms interval. Stopping or faulting releases driven fans to firmware and retains their plans for a later run. A plan whose temperature stays unavailable or whose writes fail is suspended, not removed (see "Suspended plans"). For a fan outside any control group, `set`, `boost` and `auto` remove its plan; `RestoreAll` and disposal remove all plans, and disposal also ends the run. Inside a control group under software control, `set` and `boost` replace the fan's plan with a flat one instead (see "Control groups"). The stdio executable owns a run for its process lifetime and stops it before shutdown restoration.

`FanCore.PushTemperatures(IReadOnlyList<FanTemperatureSample>)` and the same method on `FanControlSession` accept `FanTemperatureSample(string Name, double Celsius, long ObservedAtUtcTicks)` directly, without JSON or per-push dictionaries. Each push replaces the whole batch. Non-finite values and nonpositive or future UTC timestamps are dropped; observations expire after three seconds. The stdio `temp` command converts `samples` and optional `observedAtUtcTicks` into the same typed entry (omitted timestamps use the current UTC time).

Temperature kinds are public constants `FanTemperatureKinds.Cpu` (`cpu`), `.Gpu` (`gpu`), and `.Any` (`any`). Names beginning with `cpu` or `gpu`, ignoring case, belong to that kind. CPU fans read the hottest fresh CPU sample; GPU fans read the hottest fresh GPU sample. Fans with an unrecognised role read the hottest of all fresh samples and require a fresh CPU sample. Plans read by kind on every tick, including names first pushed after recording. Recording does not require a temperature: hosts subscribe only after a plan needs that kind, so a record-time check would prevent recording. Missing temperatures, including before the first drive, use the existing five-second blind timeout. At the timeout a driven fan is handed back to firmware, and a fan that was never driven is not touched. Either way the plan is kept, suspended as `Blind`. It resumes by itself when a fresh temperature of its kind arrives.

`FanCore` and `FanControlSession` expose an initial `IReadOnlyList<string> SoftwareCurveTemperatureKinds` and `event Action<IReadOnlyList<string>> SoftwareCurveTemperatureKindsChanged`. This fact lists, in the fixed order `cpu`, `gpu`, `any`, the distinct kinds needed by recorded plans that are not suspended as `Failed`. It is empty when there are no such plans. A `Blind` plan keeps its kind, so the host keeps pushing and the plan can resume. Change-only notifications use the run-needed delivery queue, outside the engine gate and in mutation order. The host subscribes and pushes only the needed kinds (`any` needs all temperatures, including CPU). `SoftwareCurveRunNeeded:true` with empty kinds means no temperatures need pushing, but the run is still needed: either hand-back retries remain, or every running plan is a fixed speed inside a software-controlled group (see "Control groups"), which is normal operation. Keep the run active whenever the fact is true. `curve`, `setcurve`, `auto`, `set`, `boost` and `group` receipts include `softwareCurveTemperatureKinds` next to `softwareCurveRunNeeded`, including unsuccessful receipts. `NeedsTemperatures` / `describe.needsTemperatures` still describe machine capability, not active demand.

`FanCore` and `FanControlSession` expose `bool SoftwareCurveRunNeeded` and `event Action<bool> SoftwareCurveRunNeededChanged`. The fact is true while a software plan that is not suspended as `Failed` is recorded, or while a fan hand-back is pending retry. Notifications carry the new value, only on changes, outside the engine gate and in change order. `curve`, `setcurve`, `auto`, `set`, `boost` and `group` replies include `softwareCurveRunNeeded`, including unsuccessful receipts. A `Failed` plan whose hand-back is still being retried keeps the fact true with no kinds. The host uses this fact to decide whether to run; the run itself keeps waiting when the fact becomes false and returns only on host cancellation or a fault. Removing a recorded plan that has no runtime entry performs no hardware release before the caller's own operation.

Both `curve` and `setcurve` receipts include `softwarePlanRecorded` (boolean) and `softwareCurveSuspension` (`null` or a `FanCurveSuspension` name, see "Suspended plans"). A recorded plan that is stopped or suspended has `softwarePlanRecorded:true`, its stored points in `curve`, and `execution:null`. `execution:"software"` means that a host-owned run is executing that fan's plan and the plan is not suspended. Firmware receipts keep their existing meaning when no software plan is recorded. On a channel with a shared takeover, `execution` is not `firmware` while any fan holds the takeover, because the firmware then executes no table (see "Control groups").

## Suspended plans

A recorded software plan is suspended instead of deleted in these cases, typed as `FanCurveSuspension`:

- `Blind`: its temperature stayed unavailable for the blind timeout. A driven fan is handed back to firmware, and a fan that was never driven is not touched. The plan resumes on the first tick that has a fresh temperature of its kind; on a channel whose takeover covers all fans, only together with its whole group (see `SharedHandBack`).
- `Failed`: the backend reported five consecutive writes as not applied, a write threw, or temporary-takeover preparation failed. The fan is handed back. The plan is not retried by itself; recording it again (`setcurve`, or applying its group again) clears the suspension and resumes it.
- `SharedHandBack`: on a channel whose takeover covers all fans, another fan's plan became `Blind`, `Failed` or `SharedTakeoverHeld`. Handing back only that fan would leave it outside firmware control at its last software value while this fan keeps the shared switch, so the engine handed every fan back and cleared the switch. The same reason marks a plan that waits for the rest of its group. Every fan of such a channel that has a plan is one group, and while no member holds the takeover the engine decides its restart for all members before clearing any suspension: the group restarts, as a whole and within one tick, only when no member is still being handed back (a failed release keeps that fan in our hands and is retried) and every suspended member can resume (`Blind` and `SharedHandBack` members have a fresh temperature; none is `Failed` or `SharedTakeoverHeld`). Until then nothing sets the shared switch, so no member leaves firmware control without a runtime that drives it and watches its temperature.
- `SharedTakeoverHeld`: the backend refused to start the shared takeover because another fan runs one of our temporary fixed-duty tables, which the switch would silently stop. Nothing was written for this fan. The plan resumes when it is recorded again. On a channel whose takeover covers all fans another member may already hold the switch (its own table passes the backend check), so the refusal ends the takeover for every member, as a `Blind` or `Failed` one does: the others are handed back as `SharedHandBack`, and the group waits until this plan is recorded again. Through `FanCore` this does not arise, because a control group switching to software hands its members' tables back first; it protects direct engine and backend use.

While suspended, the plan stays recorded with its curve, and `execution` is null. `Failed` and `SharedTakeoverHeld` plans are excluded from `SoftwareCurveRunNeeded` and `SoftwareCurveTemperatureKinds`; `Blind` and `SharedHandBack` plans keep their kinds so the host keeps pushing and they can resume. Hosts read the state from `FanCore.SoftwareCurveSuspension(int)`, `FanCore.SoftwareCurveSuspensions`, the `curve`/`setcurve` field `softwareCurveSuspension`, and `describe.softwareCurveSuspensions`. The describe field is an object keyed by fan index, for example `{"0":"Blind"}`. Every suspension is also reported once in `describe.diagnostics`.

## Error kinds

Rejected commands keep their original `error` text and add `errorKind`, the JSON name of `FanCommandErrorKind`. Typed callers get the same value from overloads ending in `out FanCommandErrorKind errorKind, out string? failureReason`:

- `FanCore.TrySetDuty(int, double, out double? appliedPercent, out FanCommandErrorKind, out string?)`
- `TrySetFullSpeed(int, out FanCommandErrorKind, out string?)`
- `TrySetAutomatic(int, bool resetFirmwareCurve, out FanCommandErrorKind, out string?)`
- `TryWriteCurve(int, IReadOnlyList<FanCurvePoint>, string? execution, out FanCommandErrorKind, out string?)`
- `TrySetGroupControl(string groupId, FanGroupControl, IReadOnlyList<FanGroupAssignment>?, out FanCommandErrorKind, out string?)`

The existing untyped overloads delegate to these, so they refuse the same requests (for example a per-fan call inside a control group) and only lose the kind.

| Kind | Meaning |
| --- | --- |
| `Other` | No typed evidence: an unrecognised backend failure or an unexpected exception. |
| `InvalidRequest` | Unparsable JSON, a non-object request (also on the stdio host, which keeps serving), a missing or mistyped field (including `id` and `op`), an unknown operation, a non-finite value, an empty curve, or a fixed speed or software-curve point outside 0 to 100 %. |
| `Unavailable` | No fan channel is open; `describe` carries the open failures. |
| `NoSuchFan` | The selected channel has no fan with this index. |
| `ReadOnly` | The fan is listed but not declared writable. |
| `Unsupported` | The channel cannot do this on this fan (no duty, firmware curve or software curve). |
| `HandBackPending` | The fan's previous software control is still being handed back; retry later. |
| `RecoveryBlocked` | An earlier session's temporary takeover is not restored; every write is rejected until a later explicit retry succeeds. `describe.recoveryBlocked` and `describe.recoveryBlockedReason` report the same state. |
| `StateUnavailable` | The takeover journal or a firmware-table baseline is missing, unreadable or cannot be saved; nothing was changed for this cause. Two exceptions, where the hardware change happened first and only recording it failed. (1) A hand-back, or a firmware curve, that the channel confirmed before the journal entry could be removed: the change stands (a `group` reply lists a handed-back fan in `handedBack` once it is under firmware), and the entry stays until a retry removes it or the next recovery releases the fan again, which keeps a completed curve and is otherwise harmless. (2) A Uniwill firmware curve that was written and runs but could not be recorded as completed: it is still treated as a temporary table, so the original table comes back at exit or at the next recovery unless the same curve is written again. |
| `RestoreUnconfirmed` | A restore was written but not confirmed; the baseline is kept for retry. |
| `SharedTakeoverHeld` | Another fan holds this channel's shared switch (or runs a temporary fixed-duty table the switch would stop), so the request would not take effect. A fixed duty, firmware curve, software drive or boost changed nothing. A hand-back (`auto` on one fan, or a `group` switch to firmware) did give up the fan's share of the switch, and that stands, but the fan is not under firmware while another fan holds the switch. Through `FanCore` a control group keeps the switch to itself, so this appears only while a member's earlier hand-back has not completed and the switch is still held; switching the group to firmware again retries that hand-back. |
| `HardwareFailed` | A hardware read or write the operation needed failed. |
| `ControlGroupSwitchRequired` | The fan belongs to a control group and the request needs the other control mode, which only the whole group can switch to. The reply adds `controlGroup` with the group id; nothing was changed. |

Only explicit evidence selects a kind: the core's own checks, or a backend that reports the kind of its own failure. A backend that implements only the untyped members reports `Other`. Kinds are never inferred from message text. Today the Uniwill backend reports typed kinds. The other backends' failures stay `Other`, as do the shared EC lock timeout and Lenovo baseline failures.

## Fixed duty and readings

A successful `set` reply includes `appliedPercent`: the percentage the channel actually wrote, after its floor and quantization, or null when the channel does not report it. `describe` reports each fan's `minimumOnPercent`. The software engine uses it for its dead zone, decided by the curve target rather than by where the ramp is: when the target is at least half the floor, a value below the floor is raised to the floor (so a fan starting from 0 goes straight to the floor and ramps from there); when the target is below half the floor, a value below the floor becomes 0, so the fan holds the floor while ramping down and then stops. No write ever lands between 0 and the floor. A fan with `canStop:false` (Acer: its custom duty takes 20 to 100 only) never gets 0 from the engine: below the floor it holds the floor. Hosts can use both to show the range. On Uniwill, any request above 0 % and below 20 % is written as 20 %, and the duty byte has 0.5 % steps.

`read` reports `automaticControl` as `true`, `false` or `null`. Null means it cannot be determined, and it is never reported as automatic. On Uniwill it is null when the mode register cannot be read. It is also null when the mode bit is clear but the temporary-table baseline cannot be read. When the shared mode bit is set, neither fan is automatic. When it is clear, a fan running one of our temporary fixed-duty tables is not automatic, and any other fan is. A persistent firmware curve counts as automatic firmware control.

## Control groups

Some channels have one switch that takes several fans away from firmware control together. Those fans form a control group: if the host switches between firmware and software control, they must switch together. `describe` lists the groups:

```json
"controlGroups": [
  { "id": "uniwill-shared-mode", "fans": [0, 1], "reason": "UniwillSharedModeBit", "firmwareFixedDuty": true, "control": "Firmware" }
]
```

- `reason` is a `FanControlGroupReason` name. `UniwillSharedModeBit`: bit 0x40 of EC register 0x0751, which takes both fans away from the firmware tables. `AcerMachineMode`: Acer's one machine-wide fan behaviour mode.
- `firmwareFixedDuty` says whether a fixed duty is executed by the firmware itself (Uniwill writes a temporary firmware table), so `set` works per fan while the group is under firmware control. On Acer a fixed duty is the machine-wide custom mode, so it is false.
- `control` is `Firmware` or `Software`, the mode last applied to the group. It is held in memory: every new session starts with every group under firmware control. A safety hand-back (below) does not change it; the suspensions say why the fans are not running.
- A fan that shares no switch is in no group, and a channel without such a switch reports an empty list. The typed equivalents are `FanCore.ControlGroups`, `ControlGroupOf(int)` and `GroupControl(string)`, from `IFanBackend.ControlGroups` (default empty).

The switch is applied with one call that names the group. The library does no staging: the host stages a change in its interface and sends the call when the user applies it.

```json
{"id":7,"op":"group","group":"uniwill-shared-mode","control":"software","fans":[{"fan":0,"curve":[{"temperatureCelsius":50,"percent":30},{"temperatureCelsius":80,"percent":80}]},{"fan":1,"percent":40}]}
{"id":8,"op":"group","group":"uniwill-shared-mode","control":"firmware"}
```

- `control` is `software` or `firmware`, ignoring case.
- To software: `fans` has exactly one entry for each member, each with either `percent` (a fixed speed) or `curve` (a software curve). Every entry is validated before anything changes; one bad entry refuses the whole call as `InvalidRequest`. When the group was under firmware control, the members' temporary fixed-duty tables are handed back first, because the shared switch would otherwise stop them silently; if that hand-back fails the call is refused and no plan is recorded. Then every plan is recorded as one change. The switch itself writes no duty: the host-owned run takes the whole group over in one tick, and a group already under software control is replaced as a whole.
- To firmware: no `fans`. One engine batch stop removes every member's plan and marks every member's runtime as being handed back in a single state change, so no run tick can drive any member again (on Acer one drive would switch the whole machine back to custom mode); then every member is handed back in order, continuing past a failed member, also past one whose hand-back threw. The group is recorded as under firmware control even when one member's hand-back failed; the run keeps retrying it, and sending the call again retries every member whose return to firmware this library has not confirmed, also one the run has meanwhile released.
- Fixed speeds are checked against what the fan accepts before any plan is recorded: 0 to 100 %; a value between 0 and the fan's `minimumOnPercent` is recorded as that floor; 0 on a fan with `canStop:false` (Acer, whose custom duty takes 20 to 100 only) is refused as `Unsupported` rather than turned into a different speed. Curve points are checked the same way: every percent from 0 to 100, else `InvalidRequest` (a point above 100 % would otherwise be refused by Acer on every write and fail the whole group). A curve point below the floor is valid, and the run's dead zone decides what it writes (see "Fixed duty and readings"): on a `canStop:false` fan the run holds the floor; otherwise a target of at least half the floor is held at the floor, and a lower target stops the fan.
- Hardware hand-back is not transactional and is not rolled back. Every reply lists `handedBack`: the members this call put back under firmware control, also when the call is refused (for example fan 0's table was handed back, then fan 1's hand-back failed). On a channel that counts holders of its shared switch (Uniwill) a member is under firmware only once no fan holds the switch: a failed switch to firmware that leaves the switch set (fan 0 gave up its share, fan 1's bit-clear failed) lists nobody. Its members stay pending: usually the run's retry then clears the switch, and the host must send the switch to firmware again (it cannot wait for a call that clears the switch by itself); that call lists every pending member, including those whose share an earlier call released. A rejection also carries the resulting `fans` (when a channel is open), so the host can refresh what it shows as applied; `fans` is null when the fans cannot be read after the switch, whose result stands either way. A rejection before the switch ran (for example a malformed `control`) lists an empty `handedBack`.
- A fault inside the switch (a transport exception during a hand-back, a journal that cannot be saved) is reported like any other failure, with `errorKind` (`Other` or `StateUnavailable`), `controlGroup`, `handedBack` as far as the call got, and `fans`. It never drops the group fields.
- `errorKind` and `error` describe one failure: the first real failure in member order; when there is none and the shared switch is still held, `SharedTakeoverHeld`; otherwise the first member whose release the channel confirmed but whose journal entry could not be removed. The state of each member comes from `handedBack` and `fans`, not from the error text.
- The reply carries `controlGroups`, `fans` and `handedBack`, plus `softwareCurveRunNeeded` and `softwareCurveTemperatureKinds`. Every rejection of a request that named a group in `group` carries `controlGroup` with that id, including a malformed `control` or `fans`; a request without a usable `group` string is a plain `InvalidRequest`.
- Commands, including `group`, must be serialized by the host, as for every other command: the library does not serialize whole mutations against each other, and a per-fan request running concurrently with a group switch can interleave with it.

Per-fan calls inside a group:

| Request | Group under firmware | Group under software |
| --- | --- | --- |
| `set` | Allowed only with `firmwareFixedDuty` (a firmware table) | The fan's own flat plan: written at once, not ramped; `appliedPercent` is null |
| `boost` | `ControlGroupSwitchRequired` | The fan's own flat 100 % plan |
| `setcurve`, software | `ControlGroupSwitchRequired` | The fan's own curve |
| `setcurve`, firmware | Allowed | `ControlGroupSwitchRequired` |
| `auto` (with or without `resetFirmwareCurve`) | Hands back the fan's own table or curve. While another fan still holds the shared switch (a group switch to firmware has not completed), the fan's share is given up but it is not under firmware: `SharedTakeoverHeld` | `ControlGroupSwitchRequired`: returning to firmware is always the whole group |

A flat plan uses a constant source that needs no temperature, so it never goes blind and adds no kind to `softwareCurveTemperatureKinds`.

Safety hand-back: when one member's plan becomes `Blind` or `Failed` (or, in direct engine use, `SharedTakeoverHeld`), the engine hands every member back to firmware, clears the shared switch, and suspends the other plans as `SharedHandBack` (see "Suspended plans"). A `Blind` member's group resumes by itself, as a whole, once that temperature and every other member's temperature are fresh and no hand-back is pending; after a `Failed` member the host applies the group again.

Below the group, the Uniwill backend still counts which fans hold the shared switch (`IFanBackend.SharedTakeoverHolders`, `SharedTakeoverCountsHolders`). Releasing one holder keeps the switch set while another holds it; a fixed duty or firmware curve is refused with `SharedTakeoverHeld` while another fan holds it; a software drive or boost is refused while another fan runs a temporary fixed-duty table. After the engine releases one fan on a holder-counting channel, it rewrites every other fan it drives on its next tick. A channel without holder counting (Acer) gets no such rewrite. Holders are in memory only: after a restart there are none, and the first release clears the switch. What the hardware does with a fan that holds no direct duty while the switch is set has not been confirmed on physical hardware.

## Explicit redetection

`FanCore` and `FanControlSession` expose typed `OpenFailureKind` and `OpenFailures` (backend, kind, reason); `describe` adds `openFailureKind` and `openFailures` beside `available`/`reason`, using the enum names `NoSupportedController`, `DriverMissing`, `AccessDenied`, and `Other` as JSON strings. When all candidates fail, precedence is `Other` > `AccessDenied` > `DriverMissing` > `NoSupportedController`: unknown failures win because the host may mark unavailable only for recognised failures and must report/restart the run for an unknown failure that may succeed on its next attempt.

Successful opens clear the overall failure kind and candidate failures. Backend exceptions become `Other` with their original message; ambiguous interface/driver absence is also `Other`, without inferring a kind from diagnostic text.

`describe` with `refresh:true` refreshes the selected backend's capabilities without changing fan ownership or settings. If initial discovery found no backend, this explicit request retries the existing discovery route in the same helper. Ordinary descriptions do not retry discovery. Availability, reason and fans in the response reflect the result after redetection. An active backend is retained; redetection never starts another writer or stops an active software plan.

## Hardware coverage

The Windows source contains WMI backends for ASUS, HP, Lenovo, and Acer, plus a Uniwill WMI/EC backend. Linux uses the kernel's `hwmon` interface. Availability and supported operations depend on the particular firmware and device. Only the Uniwill path has been exercised on physical hardware for this release; the other backends should not be taken as hardware-validated compatibility claims. HID and SMM backends are not implemented.

The desktop Super I/O route uses only a LibreHardwareMonitor `Computer` that the host has opened and passes in (see "Library hosting and local packages"); without one the route is unavailable (`NoSupportedController`), and the library never opens a `Computer` of its own. It enumerates the hardware tree as a list. LibreHardwareMonitor 0.9.6 exposes `Computer.Hardware` as `IList<IHardware>`, which the previous array-only walk never matched, so the route can now find writable headers where earlier releases found none. This route has not been exercised on physical desktop hardware. It is probed last, after the vendor firmware routes (ASUS, HP, Lenovo, Acer) and the Uniwill EC route, so a laptop whose LibreHardwareMonitor tree also exposes `Control` sensors still selects its notebook route.

On normal shutdown, the host attempts to return temporary fan takeovers to automatic control. With an explicit state directory, the library records temporary ownership before writing and the next host session restores it before allowing new writes. Abrupt termination leaves the last hardware state in place until that next session; firmware/driver failures may prevent recovery. Confirm that your device supports the requested operation before using manual fan control.

## Build

Install the .NET 10 SDK, then run:

```powershell
dotnet build src/FanControlCore/FanControlCore.csproj -c Release
```

The Windows release package is published as a self-contained single file and does not need a separately installed .NET runtime:

```powershell
dotnet publish src/FanControlCore/FanControlCore.csproj -c Release -r win-x64
```

Some Windows hardware-control paths require administrator privileges.

Protocol research referenced public implementations and documentation including NBFC, NBFC-Linux, LenovoLegionLinux, G-Helper, OmenMon, and the Linux `hwmon` ABI. Thanks to their maintainers for making that work available.

Licensed under [Apache-2.0](LICENSE).

## Firmware curve restoration

Lenovo firmware curve writes require an explicit FANCONTROLCORE_STATE_DIRECTORY supplied by the embedding host. The helper stores the original complete firmware table there, bound to the system UUID and fan ID. Resource Manager supplies its UserData/Control/FanControlCore directory. Missing device identity or unavailable storage prevents a new write.

To cancel a persistent curve, send auto with resetFirmwareCurve set to true. Success includes firmwareCurveReset: true; older helpers do not provide this receipt. Lenovo cancellation restores and verifies the saved table before releasing cooling. Missing or invalid baselines and changed firmware modes or layouts remain failures. Normal shutdown releases temporary overrides and preserves persistent firmware curves. Tables written before baseline support cannot be reconstructed automatically.

Uniwill table changes also require the host-supplied state directory and system UUID. The helper saves each fan's complete original temperature/speed bytes before mutation and the original shared custom-table enable bit. Ordinary auto and shutdown restore a temporary fixed-duty table while retaining a persistent curve. Explicit cancellation restores only the selected fan's original table; the shared bit returns to its original value after the last saved table ownership is released. Failed writes remain eligible for cleanup, failed restoration keeps its baseline for retry, and an unsuccessful first curve does not retain a persistent enable-bit claim. Editing a previously applied curve keeps a temporary copy of that curve until the new edit succeeds. Restoring one fan's table releases only that fan's share of the shared software-takeover/full-speed mode, which stays set while the other fan holds it (see "Control groups").

The Uniwill second-fan RPM low byte uses the existing 0x046D reference layout except for the verified MECHREVO JIAOLONG Series with EC project ID 0x1A, where OEM definitions and prior hardware observations identify 0x046B. Model detection is read-only and does not grant write capability. Unknown models or project IDs retain the reference layout; a failed selected register read remains unknown. Injected transport tests cover both layouts and do not access hardware.

These restoration paths have regression coverage with injected EC/WMI transports. Their new restoration behavior has not yet been exercised on physical hardware.

## Restoration failures

A release that returns false or throws an exception remains a failed restoration. The software engine keeps the restoring entry and retries it while a host-owned run is active. The first retry waits two run intervals, and each later failure doubles the wait up to five seconds, so a failing hand-back does not hit the hardware ten times a second. The old curve is no longer active. An explicit stop (`auto`, `set`, `boost`, a firmware `setcurve`, `RestoreAll`, run exit) attempts the release immediately. StopAll and the core's RestoreAll continue with the other fans and retain failed ownership for an explicit later retry. Run exit attempts restoration for every runtime entry without deleting its plan. Failed releases remain pending for an explicit later retry or a later host-owned run. Shutdown still attempts restoration. Durable restart recovery preserves unresolved ownership and rejects new writes while restoration fails; permanent backend faults require intervention.

## Curve receipts

Curve read and write receipts describe the actual execution with the existing `execution` field. Firmware table receipts may be cached until a write, cancellation or detection reset. Software receipts return the recorded plan, including while its run is stopped or the plan is suspended. `softwarePlanRecorded` distinguishes that plan from a firmware table, `execution` reports whether it is running, and `softwareCurveSuspension` reports why it is not. Read again, because a manual action may remove the plan, and a lost temperature feed or failed writes may suspend it. An unsuccessful or incomplete receipt cannot confirm application. The regression runner uses only fake backends, never hardware.

## Library hosting and local packages

`src/FanControlCore.Library` contains discovery, protocols, transports, policy, profiles and `FanCommandServer`. `src/FanControlCore` remains the thin cross-platform stdio executable. Both use the same command implementation. `FanControlSession` accepts JSON requests directly and is disposable; command callers serialize hardware operations while temperature publication may proceed independently. When embedding, supply `FanCoreOptions(StateDirectory, MachineIdentity, LibreHardwareMonitorComputer)` explicitly instead of changing process environment variables. The stdio host continues to read `FANCONTROLCORE_STATE_DIRECTORY`. `LibreHardwareMonitorPath` and `FANCONTROLCORE_LHM_PATH` are gone: the library no longer loads LibreHardwareMonitor by path, so the stdio executable has no Super I/O route.

A host that runs LibreHardwareMonitor in its own process passes its open `LibreHardwareMonitor.Hardware.Computer`, with the motherboard group enabled, as `LibreHardwareMonitorComputer`; this is the only way the Super I/O route becomes available. LibreHardwareMonitor 0.9.6 keeps process-wide statics: the CPUID/RDTSC code buffer and the ISA/PCI/EC mutexes. `Computer.Open` replaces them and `Computer.Close` frees them. A second `Computer` opened and closed in the same process would leave the host's instance with disposed mutexes, so motherboard reads and fan writes are silently skipped, and with a freed CPUID code buffer; that is why the library never opens one. The Super I/O route checks the type name and `IsMotherboardEnabled`, walks only the motherboard tree, and never opens, closes, resets or updates that `Computer`, so fan readings are as fresh as the host's own updates. The host keeps it open for as long as the fan session (`FanCore` or `FanControlSession`) is open.

`dotnet pack src/FanControlCore.Library/FanControlCore.Library.csproj -c Release --disable-build-servers -o artifacts/packages` creates the Apache-2.0 `FanControlCore.Library` package, currently `0.1.0-dev.7`. The package is not published to nuget.org; Resource Manager consumes it from a local feed. Resource Manager consumes the versioned package through its ordinary local NuGet feed alongside its other SDKs. No cross-repository source inclusion is needed by the backend.

A supplied state directory enables `temporary-takeovers.json`. Records are atomically replaced after a flush to disk and tied to machine, backend and stable fan identity. An exclusive file handle prevents concurrent owners of that directory. Uniwill identifies a fan by its firmware table (`ec-fan-table-0x0F00`, `ec-fan-table-0x0F30`), not by its display name. Records written by 0.1.0-dev.6 and earlier use the former name/role/firmware-curve identity, and those are still matched. Discovery restores pending takeovers before the first new write. Identity mismatch, unavailable hardware, unreadable state or a failed restore preserves the record and blocks new writes; explicit later operations can retry. Enumeration order alone is never used to select a recovery target. The existing Lenovo/Uniwill original-table files remain in the same directory. Uniwill temporary fixed-duty tables are restored from those files, while completed persistent firmware curves remain. Software plans, temperatures and the last duty value are not resumed. Hosts without a state directory retain normal-shutdown-only behavior, reported as `crashRecoveryEnabled:false`.

Restoration success retains each backend's existing meaning: ASUS/HP/Lenovo/Uniwill have their existing mode/table readback, while Acer acknowledges a WMI command, Super I/O relies on LHM `SetDefault`, and hwmon relies on the kernel attribute write. The latter paths do not independently prove physical fan behavior. No code can restore hardware while its host is dead; restart recovery is conditional on a working matching device and durable files.

Managed exceptions can be contained by a host boundary. In-process hosting cannot isolate native access violations, native/COM hangs, process termination, stack overflow, or a faulty kernel/firmware driver. The library has no direct P/Invoke declarations: Windows paths reach COM/WMI and OEM drivers, Super I/O reaches the supplied LHM/PawnIO stack, and Linux reaches hwmon kernel drivers through files. The existing global-to-session-local EC mutex behavior is exposed in `describe.diagnostics`, so hosts can log its cross-session limitation. The only curve timer belongs to the host-invoked run; recording a plan starts no task or timer. The engine never holds its state lock during backend, file or host-callback I/O. A separate hardware lock orders its hardware operations, so a slow backend (an EC mutex wait, a stuck WMI call) delays only hardware work. Queries, receipts, plan recording and fact notifications continue.
