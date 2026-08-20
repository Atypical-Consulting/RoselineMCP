using System.Collections;

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
/// <para>
/// This type guards <b>one exact key spelling</b>, which is only as strong as the environment block's
/// case-sensitivity. Where a test's real dependency is on a whole prefixed namespace rather than a
/// single name, use <see cref="ScopedEnvironmentNamespace"/> instead.
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

/// <summary>
/// Clears <b>every</b> environment variable that a given configuration section would see through a
/// given <c>AddEnvironmentVariables</c> prefix — in whatever casing it happens to be exported — for
/// the lifetime of a <c>using</c> scope, and restores each one to the value the scope found.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScopedEnvironmentVariable"/> guards one key, matched by the OS. That is a granularity
/// mismatch with what a provider-reading test actually depends on, and on a case-sensitive
/// environment block it is a hole rather than a guard: POSIX backs the process environment with an
/// ordinal dictionary, so <c>ROSELINE_ROSELINEMCP__CONFIRMDESTRUCTIVEWRITES</c> and
/// <c>ROSELINE_RoselineMCP__ConfirmDestructiveWrites</c> are two different variables and clearing one
/// leaves the other standing. The configuration stack, meanwhile, is case-<i>in</i>sensitive the whole
/// way down — the environment provider matches its prefix <c>OrdinalIgnoreCase</c>,
/// <c>GetSection</c> compares with <c>ConfigurationPath.KeyComparer</c>, and the options binder
/// matches properties case-insensitively — so the surviving spelling still binds and still breaks the
/// precondition the scope existed to protect. (Windows is structurally immune: its environment block
/// is case-insensitive, so the two spellings are one variable.)
/// </para>
/// <para>
/// So the guard is matched to the dependency: the property becomes "no ambient variable under this
/// prefix and section can influence this test", rather than "this one spelling cannot". Selection
/// mirrors the provider's own normalization — strip the prefix, <c>__</c> → <c>:</c> — and then
/// requires the key to fall under <paramref name="section"/>, both compared
/// <c>OrdinalIgnoreCase</c>. A prefixed variable <i>outside</i> the section is deliberately left
/// alone: it cannot reach the section's options, and clearing it would widen the blast radius over
/// variables other tests own (<c>ROSELINE_UPDATE_SCHEMA_SNAPSHOT</c> is a live example).
/// </para>
/// <para>
/// It inherits both of <see cref="ScopedEnvironmentVariable"/>'s limits verbatim — an empty-string
/// prior value cannot be restored, and the save/restore assumes strictly nested, single-threaded use
/// — and adds no others.
/// </para>
/// </remarks>
internal sealed class ScopedEnvironmentNamespace : IDisposable
{
    private const string EnvironmentKeyDelimiter = "__";
    private const string ConfigurationKeyDelimiter = ":";

    private readonly List<(string Key, string? Previous)> _captured = [];

    private ScopedEnvironmentNamespace(string prefix, string section)
    {
        var sectionPath = section + ConfigurationKeyDelimiter;

        // GetEnvironmentVariables() hands back a snapshot, so clearing inside the loop is safe.
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The same normalization EnvironmentVariablesConfigurationProvider applies. Mirrored
            // rather than taken as a dependency: it is two lines, and reaching into the provider's
            // internals would couple the test helper to a shape it does not own.
            var normalized = name[prefix.Length..].Replace(
                EnvironmentKeyDelimiter, ConfigurationKeyDelimiter, StringComparison.Ordinal);

            if (!normalized.StartsWith(sectionPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // May be null in principle; captured as-is so Dispose restores the state actually found.
            _captured.Add((name, (string?)entry.Value));
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>
    /// Clears every variable named <c><paramref name="prefix"/>…</c> whose normalized configuration
    /// key falls under <paramref name="section"/>, for the scope.
    /// </summary>
    /// <param name="prefix">The <c>AddEnvironmentVariables</c> prefix, e.g. <c>ROSELINE_</c>.</param>
    /// <param name="section">The configuration section, e.g. <c>RoselineMCP</c>.</param>
    public static ScopedEnvironmentNamespace Clear(string prefix, string section) =>
        new(prefix, section);

    public void Dispose()
    {
        foreach (var (key, previous) in _captured)
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }
}
