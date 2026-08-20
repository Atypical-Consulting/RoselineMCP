namespace RoselineMCP.Tests.Configuration;

/// <summary>
/// Sets one environment variable for the lifetime of a <c>using</c> scope and restores the value the
/// scope found — whether that was a value or nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// Tests that read the real <c>AddEnvironmentVariables</c> provider are reading process-wide state
/// they do not own. Capturing the prior value is what makes them independent of it: a test can clear
/// a variable to enforce its own precondition, and a developer who exported that variable (as
/// <c>README.md</c> § Environment Variables instructs) still finds it intact afterwards. Restoring to
/// <c>null</c> instead — the shape this type replaces — is only correct when the variable started
/// unset.
/// </para>
/// <para>
/// Two limits, both inherent rather than oversights. A prior value of the <b>empty string</b> cannot
/// be restored: <c>Environment.SetEnvironmentVariable</c> deletes the variable when handed <c>null</c>
/// <i>or</i> <c>""</c>, so the framework offers no way to put an empty value back and such a variable
/// ends the scope unset. And the save/restore is correct only under <b>strictly nested,
/// single-threaded</b> use — xUnit runs each test class as its own collection, in parallel by default,
/// so two classes scoping the same key would both capture the ambient value and the later disposer
/// would write back a stale one. Keep every class that mutates a given key in a single collection.
/// </para>
/// </remarks>
internal sealed class ScopedEnvironmentVariable : IDisposable
{
    private readonly string _key;
    private readonly string? _previous;

    private ScopedEnvironmentVariable(string key, string? value)
    {
        _key = key;
        // May be null, which is a real state to restore to and not a missing value.
        _previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    /// <summary>Sets <paramref name="key"/> for the scope; pass <c>null</c> to clear it.</summary>
    public static ScopedEnvironmentVariable Set(string key, string? value) => new(key, value);

    public void Dispose() => Environment.SetEnvironmentVariable(_key, _previous);
}
