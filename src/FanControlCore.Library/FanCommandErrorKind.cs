using System.Text.Json.Serialization;

namespace FanControlCore;

/// <summary>
/// Typed cause of a rejected command, next to its original <c>error</c> text.
///
/// Only explicit evidence selects a kind: the core's own checks, or a backend that reports the kind of a failure
/// it produced itself. The core never derives a kind from diagnostic text; anything without such evidence is
/// <see cref="Other"/>. JSON carries the member names as strings, like <see cref="FanOpenFailureKind"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FanCommandErrorKind>))]
public enum FanCommandErrorKind
{
    /// <summary>No typed evidence: unrecognised backend failures and unexpected exceptions.</summary>
    Other = 0,
    /// <summary>
    /// The request is malformed: unparsable JSON, a non-object request, a missing or mistyped field, an unknown
    /// operation, a non-finite value, an empty curve, or a fixed speed or software-curve point outside 0 to 100 %.
    /// </summary>
    InvalidRequest,
    /// <summary>No fan channel is open on this machine; <c>describe</c> carries the open failures.</summary>
    Unavailable,
    /// <summary>The selected channel has no fan with this index.</summary>
    NoSuchFan,
    /// <summary>The channel lists this fan but does not declare it writable (for example, unrecognised firmware).</summary>
    ReadOnly,
    /// <summary>The channel cannot perform this operation on this fan (no duty, firmware curve or software curve).</summary>
    Unsupported,
    /// <summary>The fan's previous software control is still being handed back to firmware; retry after it completes.</summary>
    HandBackPending,
    /// <summary>
    /// A temporary takeover left by an earlier session could not be restored. Every write stays rejected until an
    /// explicit later operation retries recovery successfully; <c>describe.recoveryBlocked</c> reports the same state.
    /// </summary>
    RecoveryBlocked,
    /// <summary>
    /// Durable state (the takeover journal or a firmware-table baseline) is missing, unreadable or cannot be saved; no fan
    /// state was changed for this cause. Two exceptions, where the hardware change happened first and only recording it
    /// failed: a hand-back or a firmware curve the channel confirmed before its journal entry could be removed (the change
    /// stands; the entry stays until a retry or the next recovery, which is harmless), and a Uniwill firmware curve that
    /// was written and runs but could not be recorded as completed (it is still treated as a temporary table, so the
    /// original table comes back at exit or at the next recovery unless the curve is written again).
    /// </summary>
    StateUnavailable,
    /// <summary>A restore was written but its readback does not confirm it; the baseline is kept for a retry.</summary>
    RestoreUnconfirmed,
    /// <summary>
    /// Another fan holds this channel's shared takeover (<see cref="FanDescription.SoftwareTakeoverCoversAllFans"/>), so
    /// the request does not take effect: a firmware-executed write (fixed duty or firmware curve) would end that
    /// takeover and nothing was changed; a software drive or full speed would silently stop another fan's temporary
    /// fixed-duty table and nothing was written; or a hand-back (a per-fan <c>auto</c>, a group switch to firmware) gave
    /// up this fan's share but the switch stays held by another fan, so the fan is not under firmware yet. Through
    /// <c>FanCore</c> it appears only while a member's earlier hand-back has not completed; switching the group to firmware
    /// again completes it.
    /// </summary>
    SharedTakeoverHeld,
    /// <summary>A hardware read or write needed by the operation failed or its readback did not confirm it.</summary>
    HardwareFailed,
    /// <summary>
    /// The fan belongs to a control group (<see cref="FanControlGroup"/>) and the request needs the other control
    /// mode, which only the whole group can switch to: software curves and full speed need the group under software
    /// control; returning to firmware, or a firmware curve, needs it under firmware control. The reply names the group.
    /// </summary>
    ControlGroupSwitchRequired
}

/// <summary>Why a recorded software curve is not being executed although a run is active.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FanCurveSuspension>))]
public enum FanCurveSuspension
{
    /// <summary>
    /// The plan's temperature stayed unavailable past the blind timeout. A fan that was being driven has been handed
    /// back to firmware; the plan is kept and resumes by itself once a fresh temperature of its kind arrives.
    /// </summary>
    Blind,
    /// <summary>
    /// Driving failed (five consecutive unconfirmed writes, an exception, or takeover preparation failed). The fan
    /// has been handed back to firmware; the plan is kept and resumes when it is recorded again.
    /// </summary>
    Failed,
    /// <summary>
    /// On a channel whose takeover covers all fans (<see cref="FanDescription.SoftwareTakeoverCoversAllFans"/>),
    /// another fan's plan was suspended as <see cref="Blind"/>, <see cref="Failed"/> or <see cref="SharedTakeoverHeld"/>. Handing only that fan back
    /// would leave it outside firmware control while this fan keeps the shared takeover, so the engine ended the
    /// takeover for every fan. The same reason marks a plan that waits for the rest of its group to become ready. The
    /// group resumes by itself, as a whole and in one tick, once no member is being handed back and every suspended
    /// member can resume: Blind and SharedHandBack members have a fresh temperature, no member is Failed or
    /// SharedTakeoverHeld.
    /// </summary>
    SharedHandBack,
    /// <summary>
    /// Driving was refused (<see cref="FanCommandErrorKind.SharedTakeoverHeld"/>): another fan runs a temporary fixed
    /// duty table that this channel's shared takeover would silently stop. The plan resumes when it is recorded again.
    /// On a channel whose takeover covers all fans the refusal also ends the takeover for every other member, which is
    /// handed back and suspended as <see cref="SharedHandBack"/>, so the group restarts only once this plan is recorded
    /// again. Through <c>FanCore</c> this does not arise: a control group switching to software ends its members'
    /// fixed-duty tables first. It protects direct engine and backend use.
    /// </summary>
    SharedTakeoverHeld
}
