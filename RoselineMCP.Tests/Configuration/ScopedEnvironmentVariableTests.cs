using Shouldly;

namespace RoselineMCP.Tests.Configuration;

/// <summary>
/// Pins the one property <see cref="ScopedEnvironmentVariable"/> exists for: the variable is
/// returned to the value the scope found, not unconditionally cleared.
/// </summary>
/// <remarks>
/// The keys below are test-only and deliberately not <c>ROSELINE_RoselineMCP__*</c>, so no run of
/// this file can disturb a setting an operator (or another test) actually reads.
/// </remarks>
public class ScopedEnvironmentVariableTests
{
    private const string UnsetKey = "ROSELINE_TEST_SCOPED_UNSET";
    private const string PresetKey = "ROSELINE_TEST_SCOPED_PRESET";
    private const string ClearedKey = "ROSELINE_TEST_SCOPED_CLEARED";

    [Fact]
    public void A_Variable_That_Was_Unset_Is_Unset_Again_After_Dispose()
    {
        Environment.SetEnvironmentVariable(UnsetKey, null);

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
        Environment.SetEnvironmentVariable(PresetKey, "original");
        try
        {
            using (ScopedEnvironmentVariable.Set(PresetKey, "other"))
            {
                Environment.GetEnvironmentVariable(PresetKey).ShouldBe("other");
            }

            Environment.GetEnvironmentVariable(PresetKey).ShouldBe("original");
        }
        finally
        {
            Environment.SetEnvironmentVariable(PresetKey, null);
        }
    }

    [Fact]
    public void Passing_Null_Clears_The_Variable_For_The_Scope_Then_Restores_It()
    {
        // How the binding tests enforce their "with the variable unset" precondition.
        Environment.SetEnvironmentVariable(ClearedKey, "exported");
        try
        {
            using (ScopedEnvironmentVariable.Set(ClearedKey, null))
            {
                Environment.GetEnvironmentVariable(ClearedKey).ShouldBeNull();
            }

            Environment.GetEnvironmentVariable(ClearedKey).ShouldBe("exported");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClearedKey, null);
        }
    }
}
