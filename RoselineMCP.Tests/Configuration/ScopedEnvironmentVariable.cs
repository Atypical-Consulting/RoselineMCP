namespace RoselineMCP.Tests.Configuration;

/// <summary>
/// Sets one environment variable for the lifetime of a <c>using</c> scope and restores whatever was
/// there before — including "nothing".
/// </summary>
/// <remarks>
/// Tests that read the real <c>AddEnvironmentVariables</c> provider are reading process-wide state
/// they do not own. Capturing the prior value is what makes them independent of it: a test can clear
/// a variable to enforce its own precondition, and a developer who exported that variable (as
/// <c>README.md</c> § Environment Variables instructs) still finds it intact afterwards. Restoring to
/// <c>null</c> instead — the shape this type replaces — is only correct when the variable started
/// unset.
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
