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

    [Fact]
    public void AddUnresolved_Does_Nothing_For_An_Empty_List()
    {
        // Arrange — the universal case: the loader removed nothing.
        var report = new AnalyzerLoadReport { AnalyzersRan = true, ReferencesConsulted = 2, ReferencesContributing = 2 };

        // Act
        report.AddUnresolved([]);

        // Assert — no note, no counter moved. ListDiagnosticsToolTests' RunAnalyzers = false case
        // reads ReferencesConsulted == 0 and must stay green.
        report.Notes.ShouldBeEmpty();
        report.ReferencesConsulted.ShouldBe(2);
        report.ReferencesContributing.ShouldBe(2);
        report.HasSomethingToReport.ShouldBeFalse();
    }

    [Fact]
    public void AddUnresolved_Names_Each_Removed_Reference_And_Counts_It_As_Consulted()
    {
        // Arrange
        var report = new AnalyzerLoadReport { AnalyzersRan = true, ReferencesConsulted = 2, ReferencesContributing = 2 };
        var missing = Path.Combine("nonexistent", "Missing.Analyzer.dll");

        // Act — the same path twice: a reference the loader removed once per project must be
        // named once, not once per project.
        report.AddUnresolved([missing, missing]);

        // Assert — consulted counts the reference the loader removed (it was there); contributing
        // does not (it never could). The path is what a caller acts on, so it is in the message.
        var note = report.Notes.ShouldHaveSingleItem();
        note.Reason.ShouldBe(AnalyzerLoadNote.Unresolved);
        note.Reference.ShouldBe("Missing.Analyzer.dll");
        note.Message.ShouldNotBeNull().ShouldContain(missing);
        note.ErrorCode.ShouldBeNull();
        report.ReferencesConsulted.ShouldBe(3);
        report.ReferencesContributing.ShouldBe(2);
    }

    [Fact]
    public void Merge_Names_The_Same_Missing_Reference_Once_Across_Projects()
    {
        // Arrange — two projects of one solution both reference the same absent analyzer.
        var missing = Path.Combine("nonexistent", "Missing.Analyzer.dll");
        var first = new AnalyzerLoadReport { AnalyzersRan = true };
        first.AddUnresolved([missing]);
        var second = new AnalyzerLoadReport { AnalyzersRan = true };
        second.AddUnresolved([missing]);

        // Act
        var merged = AnalyzerLoadReport.Merge([first, second]);

        // Assert — the counters are consultations (summed); the notes name each reference once.
        merged.Notes.ShouldHaveSingleItem().Reason.ShouldBe(AnalyzerLoadNote.Unresolved);
        merged.ReferencesConsulted.ShouldBe(2);
    }
}
