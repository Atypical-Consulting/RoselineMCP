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
            AnalyzersRan = true,
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
        json["analyzersRan"]!.GetValue<bool>().ShouldBeTrue();
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

        // Assert — a fresh report names nothing, the counters start at zero, and no pass ran.
        report.AnalyzersRan.ShouldBeFalse();
        report.ReferencesConsulted.ShouldBe(0);
        report.ReferencesContributing.ShouldBe(0);
        report.AnalyzersLoaded.ShouldBe(0);
        report.Notes.ShouldBeEmpty();
    }

    [Fact]
    public void ForResponse_Should_Keep_A_Report_That_Names_Something_And_Drop_A_Clean_One()
    {
        // Arrange
        var clean = new AnalyzerLoadReport { AnalyzersRan = true, ReferencesConsulted = 3, ReferencesContributing = 3, AnalyzersLoaded = 9 };
        var noReferences = new AnalyzerLoadReport { AnalyzersRan = true, AnalyzersLoaded = 300 };
        var degraded = new AnalyzerLoadReport
        {
            AnalyzersRan = true,
            ReferencesConsulted = 3,
            ReferencesContributing = 2,
            Notes = [new AnalyzerLoadNote { Reference = "X", Reason = AnalyzerLoadNote.LoadFailure }]
        };
        var off = new AnalyzerLoadReport { AnalyzersRan = false };

        // Act & Assert — clean stays silent, and so does a project with no analyzer references
        // whose bundled analyzers ran; degraded and "off" are reported, so a caller can tell
        // "analyzers off" from "every reference contributed" by analyzersRan, not by a zero.
        clean.HasSomethingToReport.ShouldBeFalse();
        AnalyzerLoadReport.ForResponse(clean).ShouldBeNull();
        AnalyzerLoadReport.ForResponse(noReferences).ShouldBeNull();
        AnalyzerLoadReport.ForResponse(degraded).ShouldBeSameAs(degraded);
        off.HasSomethingToReport.ShouldBeTrue();
        AnalyzerLoadReport.ForResponse(off).ShouldBeSameAs(off);
    }

    [Fact]
    public void Merge_Should_Sum_Counters_And_Name_Each_Reference_Once()
    {
        // Arrange — two projects of one solution, both referencing the same silent assembly, and
        // one of them a second, different one.
        var shared = new AnalyzerLoadNote { Reference = "Shared.Generators", Reason = AnalyzerLoadNote.NoCSharpAnalyzers };
        var first = new AnalyzerLoadReport
        {
            AnalyzersRan = true,
            ReferencesConsulted = 4,
            ReferencesContributing = 3,
            AnalyzersLoaded = 10,
            Notes = [shared]
        };
        var second = new AnalyzerLoadReport
        {
            AnalyzersRan = true,
            ReferencesConsulted = 5,
            ReferencesContributing = 3,
            AnalyzersLoaded = 12,
            Notes =
            [
                new AnalyzerLoadNote { Reference = "Shared.Generators", Reason = AnalyzerLoadNote.NoCSharpAnalyzers },
                new AnalyzerLoadNote { Reference = "Future.Analyzers", Reason = AnalyzerLoadNote.LoadFailure, ErrorCode = "ReferencesNewerCompiler" }
            ]
        };

        // Act
        var merged = AnalyzerLoadReport.Merge([first, second]);

        // Assert — reference counters count consultations; analyzersLoaded is the largest
        // per-project count (each project runs the whole bundled catalog, a sum would inflate it);
        // a reference is named once per (reference, reason).
        merged.AnalyzersRan.ShouldBeTrue();
        merged.ReferencesConsulted.ShouldBe(9);
        merged.ReferencesContributing.ShouldBe(6);
        merged.AnalyzersLoaded.ShouldBe(12);
        merged.Notes.Select(n => n.Reference).ShouldBe(["Shared.Generators", "Future.Analyzers"]);
    }

    [Fact]
    public void Merge_Of_Nothing_Should_Be_An_Empty_Report()
    {
        // Act
        var merged = AnalyzerLoadReport.Merge([]);

        // Assert — and nothing ran, which ForResponse reports rather than hides.
        merged.AnalyzersRan.ShouldBeFalse();
        merged.ReferencesConsulted.ShouldBe(0);
        merged.Notes.ShouldBeEmpty();
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
