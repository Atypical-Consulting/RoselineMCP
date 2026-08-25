using Shouldly;

namespace RoselineMCP.Tests.Configuration;

/// <summary>
/// Pins the one property <see cref="ScopedEnvironmentVariable"/> exists for: the variable is
/// returned to the value the scope found, not unconditionally cleared.
/// </summary>
/// <remarks>
/// The keys below carry no <c>ROSELINE_</c> prefix at all, which is the strongest form of the rule
/// this file follows: <b>a class that mutates process-wide environment state must pick names no
/// other collection touches.</b> <see cref="RoselineMcpOptionsBindingTests"/> both ingests every
/// <c>ROSELINE_</c>-prefixed variable through <c>AddEnvironmentVariables(prefix: "ROSELINE_")</c> and
/// clears every one of them that falls under <c>RoselineMCP:</c>. Staying outside the prefix entirely
/// puts these keys beyond the reach of both. <see cref="ScopedEnvironmentNamespaceTests"/> below
/// cannot do that — the prefix is the thing it tests — so it satisfies the same rule the other
/// available way, by staying out of the section that class clears.
/// </para>
/// <para>
/// Disjoint names are a first line, not the guarantee: because these classes mutate process-global
/// state at all, they share <see cref="ProcessEnvironmentCollection"/> and so never run concurrently
/// with one another (#189).
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public class ScopedEnvironmentVariableTests
{
    private const string UnsetKey = "ScopedEnvTests_Unset";
    private const string PresetKey = "ScopedEnvTests_Preset";
    private const string ClearedKey = "ScopedEnvTests_Cleared";
    private const string NestedKey = "ScopedEnvTests_Nested";

    [Fact]
    public void A_Variable_That_Was_Unset_Is_Unset_Again_After_Dispose()
    {
        // Establishing the precondition through the type under test rather than a bare
        // SetEnvironmentVariable: hand-rolling it here would reintroduce the restore-to-null defect
        // this class exists to disprove, one file over.
        using var ambient = ScopedEnvironmentVariable.Set(UnsetKey, null);

        using (ScopedEnvironmentVariable.Set(UnsetKey, "inside"))
        {
            Environment.GetEnvironmentVariable(UnsetKey).ShouldBe("inside");
        }

        Environment.GetEnvironmentVariable(UnsetKey).ShouldBeNull();
    }

    [Fact]
    public void A_Variable_That_Was_Set_Is_Restored_To_Its_Prior_Value()
    {
        // The case today's `SetEnvironmentVariable(key, null)` restore gets wrong: it would leave
        // the variable unset instead of returning the operator's value.
        using var ambient = ScopedEnvironmentVariable.Set(PresetKey, "original");

        using (ScopedEnvironmentVariable.Set(PresetKey, "other"))
        {
            Environment.GetEnvironmentVariable(PresetKey).ShouldBe("other");
        }

        Environment.GetEnvironmentVariable(PresetKey).ShouldBe("original");
    }

    [Fact]
    public void Passing_Null_Clears_The_Variable_For_The_Scope_Then_Restores_It()
    {
        // How the binding tests enforce their "with the variable unset" precondition.
        using var ambient = ScopedEnvironmentVariable.Set(ClearedKey, "exported");

        using (ScopedEnvironmentVariable.Set(ClearedKey, null))
        {
            Environment.GetEnvironmentVariable(ClearedKey).ShouldBeNull();
        }

        Environment.GetEnvironmentVariable(ClearedKey).ShouldBe("exported");
    }

    [Fact]
    public void Nested_Scopes_On_One_Key_Unwind_To_The_Ambient_Value()
    {
        // The exact shape both binding tests rely on: an outer scope clears the variable to enforce
        // the "unset" precondition, an inner scope sets the value under test. Each scope must
        // capture the value *it* found — an implementation that memoized the ambient value per key,
        // or skipped the write when the value looked unchanged, would keep the three tests above
        // green while silently destroying an operator's export here.
        using var ambient = ScopedEnvironmentVariable.Set(NestedKey, "ambient");

        using (ScopedEnvironmentVariable.Set(NestedKey, null))
        {
            Environment.GetEnvironmentVariable(NestedKey).ShouldBeNull();

            using (ScopedEnvironmentVariable.Set(NestedKey, "inner"))
            {
                Environment.GetEnvironmentVariable(NestedKey).ShouldBe("inner");
            }

            Environment.GetEnvironmentVariable(NestedKey).ShouldBeNull();
        }

        Environment.GetEnvironmentVariable(NestedKey).ShouldBe("ambient");
    }
}

/// <summary>
/// Pins the property <see cref="ScopedEnvironmentNamespace"/> exists for: <b>every</b> casing of a
/// prefixed variable the target section would see is cleared for the scope, and every one of them
/// comes back exactly as the scope found it.
/// </summary>
/// <remarks>
/// <para>
/// The keys below carry the real <c>ROSELINE_</c> prefix — how that prefix is matched is the thing
/// under test, so faking it would test something else — but they sit in a <b>probe section</b>
/// (<c>ScopedNsProbe:</c>), never <c>RoselineMCP:</c>. That is deliberate and load-bearing.
/// <see cref="RoselineMcpOptionsBindingTests"/> scopes the whole <c>ROSELINE_</c> →
/// <c>RoselineMCP:</c> namespace. Two classes clearing and restoring the same variables concurrently
/// is a race whichever way it lands; disjoint sections keep them independent even before
/// <see cref="ProcessEnvironmentCollection"/> stops them overlapping in time. The probe keys are
/// still ingested by that class's <c>AddEnvironmentVariables(prefix: "ROSELINE_")</c>, but they
/// land under <c>ScopedNsProbe:</c>, which its <c>GetSection("RoselineMCP")</c> never reads.
/// </para>
/// <para>
/// The section name is passed mixed-case while several keys are all-caps, on purpose: the section
/// match is <c>OrdinalIgnoreCase</c>, and that asymmetry — a case-sensitive OS environment block
/// under a case-insensitive configuration stack — is exactly what this type exists to absorb.
/// </para>
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public class ScopedEnvironmentNamespaceTests
{
    private const string Prefix = "ROSELINE_";
    private const string Section = "ScopedNsProbe";
    private const string UnrelatedSection = "ScopedNsNothingHere";

    private const string AllCapsKey = "ROSELINE_SCOPEDNSPROBE__ALLCAPSPROBE";
    private const string BothMixedKey = "ROSELINE_ScopedNsProbe__BothProbe";
    private const string BothAllCapsKey = "ROSELINE_SCOPEDNSPROBE__BOTHPROBE";
    private const string OtherSectionKey = "ROSELINE_ScopedNsOutside__Probe";
    private const string NoSectionKey = "ROSELINE_SCOPEDNSPROBEUNSECTIONED";

    [Fact]
    public void An_All_Caps_Spelling_Is_Cleared_For_The_Scope_And_Restored_After()
    {
        // The hole this type closes: on a case-sensitive environment block an all-caps name is a
        // *different* variable from the documented mixed-case one, so a per-key scope never touches
        // it — while the case-insensitive configuration stack binds it just the same.
        using var ambient = ScopedEnvironmentVariable.Set(AllCapsKey, "exported");

        using (ScopedEnvironmentNamespace.Clear(Prefix, Section))
        {
            Environment.GetEnvironmentVariable(AllCapsKey).ShouldBeNull();
        }

        Environment.GetEnvironmentVariable(AllCapsKey).ShouldBe("exported");
    }

    [Fact]
    public void Both_Casings_Set_At_Once_Are_Cleared_And_Both_Are_Restored()
    {
        using var ambientMixed = ScopedEnvironmentVariable.Set(BothMixedKey, "mixed");
        using var ambientAllCaps = ScopedEnvironmentVariable.Set(BothAllCapsKey, "caps");

        // Read what the process actually holds instead of branching on the OS: on a case-sensitive
        // block (Linux, macOS) these are two variables holding "mixed" and "caps"; on a
        // case-insensitive one (Windows) they are a single variable the second Set overwrote, so
        // both spellings read "caps". "Restored as found" is the invariant on either, and asserting
        // it against the observed prior values says exactly that without an OS switch.
        var mixedBefore = Environment.GetEnvironmentVariable(BothMixedKey);
        var allCapsBefore = Environment.GetEnvironmentVariable(BothAllCapsKey);

        using (ScopedEnvironmentNamespace.Clear(Prefix, Section))
        {
            Environment.GetEnvironmentVariable(BothMixedKey).ShouldBeNull();
            Environment.GetEnvironmentVariable(BothAllCapsKey).ShouldBeNull();
        }

        Environment.GetEnvironmentVariable(BothMixedKey).ShouldBe(mixedBefore);
        Environment.GetEnvironmentVariable(BothAllCapsKey).ShouldBe(allCapsBefore);
    }

    [Fact]
    public void A_Prefixed_Variable_Outside_The_Section_Is_Left_Untouched()
    {
        // What keeps the blast radius narrow, and the reason the filter is not "starts with the
        // prefix". ROSELINE_UPDATE_SCHEMA_SNAPSHOT — read by ToolSchemaSnapshotTests, in yet
        // another parallel collection — is a live instance of the second shape below: prefixed, no
        // "__" at all, so it normalizes to no section and must survive untouched.
        using var otherSection = ScopedEnvironmentVariable.Set(OtherSectionKey, "other-section");
        using var noSection = ScopedEnvironmentVariable.Set(NoSectionKey, "no-section");

        using (ScopedEnvironmentNamespace.Clear(Prefix, Section))
        {
            Environment.GetEnvironmentVariable(OtherSectionKey).ShouldBe("other-section");
            Environment.GetEnvironmentVariable(NoSectionKey).ShouldBe("no-section");
        }

        Environment.GetEnvironmentVariable(OtherSectionKey).ShouldBe("other-section");
        Environment.GetEnvironmentVariable(NoSectionKey).ShouldBe("no-section");
    }

    [Fact]
    public void Clearing_A_Different_Section_Captures_Nothing_And_Leaves_This_One_Alone()
    {
        // The dual of the test above, and the half that is easy to get vacuously wrong: hold a
        // variable that IS in `Section`, then scope a *different* section. Nothing may be captured,
        // so the variable must read unchanged inside the scope as well as after it — and dispose,
        // having captured nothing, must not write anything back either.
        using var ambient = ScopedEnvironmentVariable.Set(AllCapsKey, "exported");

        using (ScopedEnvironmentNamespace.Clear(Prefix, UnrelatedSection))
        {
            Environment.GetEnvironmentVariable(AllCapsKey).ShouldBe("exported");
        }

        Environment.GetEnvironmentVariable(AllCapsKey).ShouldBe("exported");
    }

    [Theory]
    [InlineData("", "RoselineMCP")]
    [InlineData("   ", "RoselineMCP")]
    [InlineData("ROSELINE_", "")]
    [InlineData("ROSELINE_", "RoselineMCP:")]
    public void An_Argument_That_Could_Never_Match_Is_Refused_Rather_Than_Silently_Clearing_Nothing(
        string prefix, string section)
    {
        // Each of these builds a filter no key can satisfy, so without the guard the caller gets a
        // live scope that cleared nothing — and their ambient export survives into the assertion the
        // scope was meant to protect. A silent no-op is the one failure mode this type must not have.
        Should.Throw<ArgumentException>(() => ScopedEnvironmentNamespace.Clear(prefix, section));
    }
}
