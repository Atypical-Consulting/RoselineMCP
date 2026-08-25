using System.Collections;
using Microsoft.Extensions.Configuration;

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
/// would write back a stale one. Keep every class that mutates a given key in
/// <see cref="ProcessEnvironmentCollection"/>, which disables parallelization;
/// <c>ProcessEnvironmentCollectionTests</c> pins that membership.
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
/// Each variable is cleared <b>through <see cref="ScopedEnvironmentVariable"/> itself</b> rather than
/// by a second copy of its capture/restore. That is what makes the empty-string limit documented
/// there apply here by construction instead of by prose, and it is what lets a failure part-way
/// through the sweep unwind the variables already taken (below) instead of deleting them for the rest
/// of the process.
/// </para>
/// <para>
/// ⚠️ It does <b>not</b> inherit the single-collection rule unchanged — it <b>widens</b> it. The
/// per-key scope asks only that classes mutating <i>the same key</i> share a collection; this one
/// captures every variable under the prefix and section at once, so what it needs is that no parallel
/// collection mutates or reads <i>any</i> of them for the duration. Two classes did clear the same
/// <c>ROSELINE_</c> → <c>RoselineMCP:</c> namespace without sharing one — the intermittent failure
/// #189 traced — which is why <see cref="ProcessEnvironmentCollection"/> now holds every such class
/// and <c>ProcessEnvironmentCollectionTests</c> pins the membership by scanning for these call sites.
/// A new class that clears an overlapping section is therefore a red bar rather than a new flake.
/// </para>
/// </remarks>
internal sealed class ScopedEnvironmentNamespace : IDisposable
{
    // The provider's environment-side delimiter. Unlike ConfigurationPath.KeyDelimiter this one has
    // no public constant to point at — it lives inside EnvironmentVariablesConfigurationProvider —
    // so it is the one piece of the normalization that must be spelled out here.
    private const string EnvironmentKeyDelimiter = "__";

    private readonly List<ScopedEnvironmentVariable> _cleared = [];

    private ScopedEnvironmentNamespace(string prefix, string section)
    {
        // A wrong argument here is otherwise a SILENT no-op: the scope still constructs, still
        // returns something disposable, and the ambient variable it was meant to neutralize sails
        // straight through into the assertion. Refusing the spellings that cannot match anything is
        // cheap, and it turns that into a stack trace at the call site.
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(section);

        if (section.Contains(ConfigurationPath.KeyDelimiter, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Pass the section name alone; the '{ConfigurationPath.KeyDelimiter}' separator is " +
                $"added here, so '{section}' would never match a key.",
                nameof(section));
        }

        var sectionPath = section + ConfigurationPath.KeyDelimiter;

        try
        {
            // GetEnvironmentVariables() hands back a snapshot, so clearing inside the loop is safe.
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var name = (string)entry.Key;
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The same normalization EnvironmentVariablesConfigurationProvider applies. Mirrored
                // rather than taken as a dependency on its internals — but mirrored against the
                // framework's own delimiter constant, so only the half that has no constant is
                // spelled by hand.
                var normalized = name[prefix.Length..].Replace(
                    EnvironmentKeyDelimiter, ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);

                if (!normalized.StartsWith(sectionPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _cleared.Add(ScopedEnvironmentVariable.Set(name, null));
            }
        }
        catch
        {
            // The caller never gets the instance, so its `using` will never run: without this the
            // variables cleared before the throw would stay deleted for the rest of the process,
            // and every later test reading them would quietly see defaults.
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Clears every variable named <c><paramref name="prefix"/>…</c> whose normalized configuration
    /// key falls under <paramref name="section"/>, for the scope.
    /// </summary>
    /// <param name="prefix">The <c>AddEnvironmentVariables</c> prefix, e.g. <c>ROSELINE_</c>.</param>
    /// <param name="section">The configuration section, e.g. <c>RoselineMCP</c> — the name on its own,
    /// without a trailing separator.</param>
    public static ScopedEnvironmentNamespace Clear(string prefix, string section) =>
        new(prefix, section);

    public void Dispose()
    {
        // Reverse order, so the scopes unwind the way nested `using`s would, and cleared afterwards
        // so a second Dispose cannot replay stale values over whatever the environment holds by then.
        for (var i = _cleared.Count - 1; i >= 0; i--)
        {
            _cleared[i].Dispose();
        }

        _cleared.Clear();
    }
}
