namespace RoselineMCP.Tests.Configuration;

/// <summary>
/// The one collection every test class that mutates the process environment belongs to, and the
/// reason none of them run concurrently.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScopedEnvironmentVariable"/> and <see cref="ScopedEnvironmentNamespace"/> save and
/// restore process-global state, which is correct only under strictly nested, single-threaded use.
/// xunit runs each test class as its own collection, in parallel by default, so two classes scoping
/// the same key — or, for the namespace scope, any key under the same prefix and section — both
/// capture the ambient value and the later disposer writes back a stale one. That is the race behind
/// the intermittent
/// <c>RoselineMcpOptionsBindingTests.An_Ambient_All_Caps_Export_Cannot_Change_What_These_Tests_See</c>
/// failure (#189): it and <c>GuardOptionsTests</c> both clear <c>ROSELINE_</c> under
/// <c>RoselineMCP</c>, and neither belonged to a collection.
/// </para>
/// <para>
/// Sharing a collection is what serializes these classes against <i>each other</i>;
/// <c>DisableParallelization</c> goes further, and deliberately so — xunit documents it as
/// "determines whether tests in this collection runs in parallel with <b>any other collections</b>",
/// so the collection also overlaps nothing else in the suite. That breadth is the point:
/// <see cref="ScopedEnvironmentNamespace"/> deletes <i>every</i> variable under a prefix and section
/// for the duration of the scope, so mutual exclusion between its own members would not stop an
/// unrelated parallel test from reading them while they are gone. The cost is bounded — these classes
/// take well under a second combined — and the rest of the suite still runs in parallel with itself.
/// It is the same idiom <c>RoselineMCP.Tests.Protocol.McpProtocolCollection</c> already applies
/// around a shared cache.
/// </para>
/// <para>
/// Membership is pinned by <see cref="ProcessEnvironmentCollectionTests"/>, so a class that starts
/// scoping the environment outside this collection is a red bar rather than a new flake. The pin is
/// a source scan for the two helpers and the raw <c>Environment.SetEnvironmentVariable</c> call they
/// replaced, so it catches what is written at the call site — reaching the environment through some
/// further indirection of one's own would still slip past it.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ProcessEnvironmentCollection
{
    public const string Name = "Process environment (sequential — scoped environment mutation)";
}
