using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using RoselineMCP.Models;
using Shouldly;

namespace RoselineMCP.Tests.Models;

/// <summary>
/// Wire contract of <see cref="AnalyzerLoadReport"/> / <see cref="AnalyzerLoadNote"/> — the block
/// that names every analyzer reference that contributed nothing (#183) — and of the
/// <see cref="DiagnosticComputationResult"/> that carries it out of the diagnostics pass.
/// </summary>
public class AnalyzerLoadReportTests
{
    [Fact]
    public void Should_Serialize_Counts_And_Notes_With_CamelCase_Names()
    {
        // Arrange — one reference out of three contributed nothing.
        var report = new AnalyzerLoadReport
        {
            ReferencesConsulted = 3,
            ReferencesContributing = 2,
            AnalyzersLoaded = 41,
            Notes =
            [
                new AnalyzerLoadNote
                {
                    Reference = "Microsoft.CodeAnalysis.NetAnalyzers",
                    Reason = AnalyzerLoadNote.NoCSharpAnalyzers
                }
            ]
        };

        // Act
        var json = JsonNode.Parse(JsonSerializer.Serialize(report))!.AsObject();

        // Assert — the names are the contract (docs/API.md and the website mirror them).
        json["referencesConsulted"]!.GetValue<int>().ShouldBe(3);
        json["referencesContributing"]!.GetValue<int>().ShouldBe(2);
        json["analyzersLoaded"]!.GetValue<int>().ShouldBe(41);
        var note = json["notes"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        note["reference"]!.GetValue<string>().ShouldBe("Microsoft.CodeAnalysis.NetAnalyzers");
        note["reason"]!.GetValue<string>().ShouldBe("no C# analyzers");
    }

    [Fact]
    public void Should_Omit_Message_And_ErrorCode_When_Null()
    {
        // Arrange — "no C# analyzers" has nothing more to say; an always-present null would
        // spend tokens on the overwhelmingly common case.
        var note = new AnalyzerLoadNote { Reference = "Some.Generator", Reason = AnalyzerLoadNote.NoCSharpAnalyzers };

        // Act
        var json = JsonNode.Parse(JsonSerializer.Serialize(note))!.AsObject();

        // Assert
        json.ContainsKey("message").ShouldBeFalse();
        json.ContainsKey("errorCode").ShouldBeFalse();
    }

    [Fact]
    public void Should_Carry_Message_And_ErrorCode_For_A_Load_Failure()
    {
        // Arrange — Roslyn's own diagnosis of why the reference yielded nothing.
        var note = new AnalyzerLoadNote
        {
            Reference = "Microsoft.CodeAnalysis.NetAnalyzers",
            Reason = AnalyzerLoadNote.LoadFailure,
            ErrorCode = "ReferencesNewerCompiler",
            Message = "references a newer compiler (5.9.0.0) than the one loaded (5.6.0.0)"
        };

        // Act
        var json = JsonNode.Parse(JsonSerializer.Serialize(note))!.AsObject();

        // Assert
        json["reason"]!.GetValue<string>().ShouldBe("load-failure");
        json["errorCode"]!.GetValue<string>().ShouldBe("ReferencesNewerCompiler");
        json["message"]!.GetValue<string>().ShouldContain("newer compiler");
    }

    [Fact]
    public void Should_Default_To_An_Empty_Report()
    {
        // Act
        var report = new AnalyzerLoadReport();

        // Assert — a fresh report names nothing; the counters start at zero.
        report.ReferencesConsulted.ShouldBe(0);
        report.ReferencesContributing.ShouldBe(0);
        report.AnalyzersLoaded.ShouldBe(0);
        report.Notes.ShouldBeEmpty();
    }

    [Fact]
    public void DiagnosticComputationResult_Should_Carry_Diagnostics_And_Report()
    {
        // Arrange
        var report = new AnalyzerLoadReport { ReferencesConsulted = 1, ReferencesContributing = 1, AnalyzersLoaded = 5 };

        // Act
        var result = new DiagnosticComputationResult
        {
            Diagnostics = ImmutableArray<Diagnostic>.Empty,
            AnalyzerLoad = report
        };

        // Assert
        result.Diagnostics.ShouldBeEmpty();
        result.AnalyzerLoad.ShouldBeSameAs(report);
    }
}
