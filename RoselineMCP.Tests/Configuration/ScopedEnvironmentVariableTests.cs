using Shouldly;

namespace RoselineMCP.Tests.Configuration;

/// <summary>
/// Pins the one property <see cref="ScopedEnvironmentVariable"/> exists for: the variable is
/// returned to the value the scope found, not unconditionally cleared.
/// </summary>
/// <remarks>
/// The keys below are deliberately <b>not</b> <c>ROSELINE_</c>-prefixed.
/// <see cref="RoselineMcpOptionsBindingTests"/> is a separate xUnit collection — so it runs in
/// parallel with this one by default — and its <c>AddEnvironmentVariables(prefix: "ROSELINE_")</c>
/// ingests <i>every</i> variable carrying that prefix, not just the two it asserts on. Staying
/// outside the prefix is what makes the two classes actually independent, rather than independent
/// by the accident of which section a key lands in.
/// </remarks>
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
