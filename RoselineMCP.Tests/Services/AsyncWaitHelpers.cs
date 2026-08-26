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
    /// Awaits <paramref name="signal"/> up to <paramref name="timeout"/> and asserts it completed.
    /// For a bare hang-guard on a signal task whose result is not needed — e.g. "did this
    /// background continuation get scheduled".
    /// </summary>
    public static async Task WaitForSignal(Task signal, TimeSpan timeout, string label)
    {
        await Task.WhenAny(signal, Task.Delay(timeout));
        signal.IsCompleted.ShouldBeTrue(
            $"{label} did not complete within {timeout} — this is a scheduling-latency safety " +
            "net, not the test's real assertion; if this trips repeatedly under CI load, raise " +
            "the timeout further before suspecting a real regression.");
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
        finished.ShouldBeSameAs(operation,
            $"{label} did not complete within {timeout} — this is a scheduling-latency safety " +
            "net, not the test's real assertion; if this trips repeatedly under CI load, raise " +
            "the timeout further before suspecting a real regression.");
        return await operation;
    }
}
