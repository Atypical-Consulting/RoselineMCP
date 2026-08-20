using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// The real <see cref="VerificationService"/>, wired the way production wires it (compiler-only),
/// for tests that need a working compile gate rather than a fake of one. Verification is the thing
/// standing between a tool call and a broken working tree, so faking it by default across the suite
/// would quietly remove the gate from every test that is not about the gate.
/// </summary>
internal static class TestVerification
{
    public static IVerificationService New() =>
        new VerificationService(A.Fake<ILogger<VerificationService>>(), DiagnosticComputationService.CompilerOnly);
}
