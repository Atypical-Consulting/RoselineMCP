using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Shared hang-guard helpers for tests that race a background operation's completion against a
/// wall-clock ceiling. The ceiling's only job is "don't hang the suite forever if the operation
/// under test genuinely never happens" — it is not meant to double as a correctness assertion, so
/// callers should size <c>timeout</c> generously. Introduced for <c>GuardServiceTests</c> (#219,
/// where a 30s ceiling doubled as a correctness gate on a background continuation's scheduling and
/// tripped on a loaded Windows CI runner) and shared with <c>ElicitationTests</c> (#224, the same
/// shape at a tighter 15s/20s ceiling) rather than duplicated a second time.
/// </summary>
internal static class AsyncWaitHelpers
{
    /// <summary>
    /// The message both waits below fail with: the ceiling exists purely as a hang-guard, so a trip
    /// reads as a scheduling-latency note rather than a claim that the operation is broken.
    /// </summary>
    private static string TimeoutMessage(string label, string verb, TimeSpan timeout) =>
        $"{label} did not {verb} within {timeout} — this is a scheduling-latency safety net, not " +
        "the test's real assertion; if this trips repeatedly under CI load, raise the timeout " +
        "further before suspecting a real regression.";

    /// <summary>
    /// Awaits <paramref name="signal"/> up to <paramref name="timeout"/>, asserts it — not the
    /// delay — is what finished the race, and re-awaits it so a Faulted/Canceled signal still
    /// surfaces its exception instead of being reported as a silent pass. For a hang-guard on a
    /// signal task whose result is not needed — e.g. "did this background continuation get
    /// scheduled". <paramref name="verb"/> names what the signal was waited on to do (default
    /// <c>"complete"</c>) — e.g. <c>"enter VerifyAsync"</c> — so the failure message stays as
    /// precise as a call site's own hand-written wait was, rather than genericizing every caller's
    /// diagnostic to the same word.
    /// </summary>
    public static async Task WaitForSignal(Task signal, TimeSpan timeout, string label, string verb = "complete")
    {
        var finished = await Task.WhenAny(signal, Task.Delay(timeout));
        finished.ShouldBeSameAs(signal, TimeoutMessage(label, verb, timeout));

        // Task.WhenAny completes on Faulted/Canceled just as readily as on success, so the check
        // above only proves the delay didn't win the race — it does not prove the signal itself
        // succeeded. Re-awaiting it observes (and re-throws) any exception, the same way
        // WaitForCompletion below does by awaiting `operation` after the race.
        await signal;
    }

    /// <summary>
    /// Awaits <paramref name="operation"/> up to <paramref name="timeout"/>, asserts it — not the
    /// delay — is what finished the race, and returns its result. For sites where a
    /// <c>Task.WhenAny</c> ceiling exists as a hang-guard but the operation's result is still
    /// needed afterward.
    /// </summary>
    public static async Task<T> WaitForCompletion<T>(Task<T> operation, TimeSpan timeout, string label)
    {
        var finished = await Task.WhenAny(operation, Task.Delay(timeout));
        finished.ShouldBeSameAs(operation, TimeoutMessage(label, "complete", timeout));
        return await operation;
    }
}
