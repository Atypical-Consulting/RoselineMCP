using System.Reflection;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using RoselineMCP.Models;
using RoselineMCP.Services;
using RoselineMCP.Interfaces;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for SolutionAnalyzerService internal/private helper methods via reflection.
/// These test the pure business logic without requiring an actual MSBuild workspace.
/// </summary>
public class SolutionAnalyzerServiceInternalTests : IDisposable
{
    private readonly SolutionAnalyzerService _sut;
    private readonly IDiagnosticFilterService _realFilterService;
    private readonly string _testDirectory;

    public SolutionAnalyzerServiceInternalTests()
    {
        var logger = A.Fake<ILogger<SolutionAnalyzerService>>();
        var msBuildService = A.Fake<IMSBuildService>();
        var codeFixProviderFactory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>());
        _realFilterService = new DiagnosticFilterService(codeFixProviderFactory);
        _sut = new SolutionAnalyzerService(logger, msBuildService, _realFilterService, A.Fake<IProjectLoader>());

        _testDirectory = Path.Combine(Path.GetTempPath(), $"RoselineTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, true); } catch { /* ignored */ }
    }

    #region Helper methods

    private T InvokePrivate<T>(string methodName, params object?[] args)
    {
        var method = typeof(SolutionAnalyzerService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull($"Method '{methodName}' not found");
        return (T)method!.Invoke(_sut, args)!;
    }

    private void InvokePrivateVoid(string methodName, params object?[] args)
    {
        var method = typeof(SolutionAnalyzerService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull($"Method '{methodName}' not found");
        method!.Invoke(_sut, args);
    }

    #endregion

    #region IsGitUrl

    public class IsGitUrlTests : SolutionAnalyzerServiceInternalTests
    {
        [Theory]
        [InlineData("https://github.com/test/repo.git")]
        [InlineData("http://github.com/test/repo")]
        [InlineData("https://bitbucket.org/user/repo")]
        public void Should_Return_True_For_Http_Urls(string url)
        {
            var result = InvokePrivate<bool>("IsGitUrl", url);
            result.ShouldBeTrue();
        }

        [Theory]
        [InlineData("/home/user/project")]
        [InlineData("C:\\Projects\\MyApp")]
        [InlineData("relative/path/to/project")]
        [InlineData("./my-project")]
        public void Should_Return_False_For_Local_Paths(string path)
        {
            var result = InvokePrivate<bool>("IsGitUrl", path);
            result.ShouldBeFalse();
        }
    }

    #endregion

    #region FindSolutionInDirectory

    public class FindSolutionInDirectoryTests : SolutionAnalyzerServiceInternalTests
    {
        [Fact]
        public void Should_Return_Sln_File_When_Present()
        {
            // Arrange
            var slnPath = Path.Combine(_testDirectory, "MySolution.sln");
            File.WriteAllText(slnPath, "placeholder");

            // Act
            var result = InvokePrivate<string>("FindSolutionInDirectory", _testDirectory);

            // Assert
            result.ShouldBe(slnPath);
        }

        [Fact]
        public void Should_Throw_When_No_Sln_Present()
        {
            // Act & Assert
            Should.Throw<TargetInvocationException>(() =>
                InvokePrivate<string>("FindSolutionInDirectory", _testDirectory))
                .InnerException.ShouldBeOfType<FileNotFoundException>();
        }

        [Fact]
        public void Should_Return_First_Sln_When_Multiple_Present()
        {
            // Arrange
            var sln1 = Path.Combine(_testDirectory, "Alpha.sln");
            var sln2 = Path.Combine(_testDirectory, "Beta.sln");
            File.WriteAllText(sln1, "placeholder");
            File.WriteAllText(sln2, "placeholder");

            // Act
            var result = InvokePrivate<string>("FindSolutionInDirectory", _testDirectory);

            // Assert
            result.ShouldNotBeNullOrEmpty();
            result.ShouldEndWith(".sln");
        }
    }

    #endregion

    // NOTE: the former private project-resolution helpers (IsValidProjectFile,
    // TryFindProjectInDirectory, GetStartDirectory, GetParentDirectory, TryFindSolutionInDirectory,
    // FindSolutionFile, ResolveProjectPath, LoadProjectAsync, FindProjectInSolution) were deleted:
    // ListDiagnostics now loads through the shared IProjectLoader, whose resolution behavior is
    // covered by ProjectLoaderTests.

    #region UpdateSummary

    public class UpdateSummaryTests : SolutionAnalyzerServiceInternalTests
    {
        [Fact]
        public void Should_Increment_Error_Count()
        {
            // Arrange
            var summary = new DiagnosticSummary();

            // Act
            InvokePrivateVoid("UpdateSummary", summary, DiagnosticSeverity.Error);

            // Assert
            summary.Error.ShouldBe(1);
            summary.Warning.ShouldBe(0);
            summary.Info.ShouldBe(0);
            summary.Hidden.ShouldBe(0);
        }

        [Fact]
        public void Should_Increment_Warning_Count()
        {
            // Arrange
            var summary = new DiagnosticSummary();

            // Act
            InvokePrivateVoid("UpdateSummary", summary, DiagnosticSeverity.Warning);

            // Assert
            summary.Warning.ShouldBe(1);
            summary.Error.ShouldBe(0);
        }

        [Fact]
        public void Should_Increment_Info_Count()
        {
            // Arrange
            var summary = new DiagnosticSummary();

            // Act
            InvokePrivateVoid("UpdateSummary", summary, DiagnosticSeverity.Info);

            // Assert
            summary.Info.ShouldBe(1);
        }

        [Fact]
        public void Should_Increment_Hidden_Count()
        {
            // Arrange
            var summary = new DiagnosticSummary();

            // Act
            InvokePrivateVoid("UpdateSummary", summary, DiagnosticSeverity.Hidden);

            // Assert
            summary.Hidden.ShouldBe(1);
        }

        [Fact]
        public void Should_Accumulate_Multiple_Calls()
        {
            // Arrange
            var summary = new DiagnosticSummary();

            // Act
            InvokePrivateVoid("UpdateSummary", summary, DiagnosticSeverity.Error);
            InvokePrivateVoid("UpdateSummary", summary, DiagnosticSeverity.Error);
            InvokePrivateVoid("UpdateSummary", summary, DiagnosticSeverity.Warning);

            // Assert
            summary.Error.ShouldBe(2);
            summary.Warning.ShouldBe(1);
        }
    }

    #endregion

    #region CreateDiagnosticDetail

    public class CreateDiagnosticDetailTests : SolutionAnalyzerServiceInternalTests
    {
        [Fact]
        public void Should_Create_Detail_From_Diagnostic()
        {
            // Arrange
            var descriptor = new DiagnosticDescriptor(
                "CS0168", "Test title", "Test {0} message", "Test",
                DiagnosticSeverity.Warning, isEnabledByDefault: true);
            var diagnostic = Diagnostic.Create(descriptor, Location.None, "arg");
            var projectName = "MyProject";

            // Act
            var result = InvokePrivate<DiagnosticDetail>("CreateDiagnosticDetail", diagnostic, projectName);

            // Assert
            result.ShouldNotBeNull();
            result.Project.ShouldBe(projectName);
            result.Id.ShouldBe("CS0168");
            result.Severity.ShouldBe("warning");
            result.Message.ShouldContain("Test");
        }

        [Fact]
        public void Should_Set_Unknown_File_When_No_Location()
        {
            // Arrange
            var descriptor = new DiagnosticDescriptor(
                "CS0219", "Test", "Message", "Test",
                DiagnosticSeverity.Warning, isEnabledByDefault: true);
            var diagnostic = Diagnostic.Create(descriptor, Location.None);

            // Act
            var result = InvokePrivate<DiagnosticDetail>("CreateDiagnosticDetail", diagnostic, "Proj");

            // Assert
            result.File.ShouldBe("Unknown");
        }

        [Fact]
        public void Should_Map_Error_Severity()
        {
            // Arrange
            var descriptor = new DiagnosticDescriptor(
                "CS0001", "Test", "Error {0}", "Test",
                DiagnosticSeverity.Error, isEnabledByDefault: true);
            var diagnostic = Diagnostic.Create(descriptor, Location.None, "msg");

            // Act
            var result = InvokePrivate<DiagnosticDetail>("CreateDiagnosticDetail", diagnostic, "P");

            // Assert
            result.Severity.ShouldBe("error");
        }
    }

    #endregion

    #region OrderDiagnostics

    public class OrderDiagnosticsTests : SolutionAnalyzerServiceInternalTests
    {
        [Fact]
        public void Should_Order_By_Severity_Priority_Descending()
        {
            // Arrange
            var diagnostics = new List<DiagnosticDetail>
            {
                new() { Id = "CS0001", Severity = "info", File = "a.cs", Line = 1 },
                new() { Id = "CS0002", Severity = "error", File = "a.cs", Line = 1 },
                new() { Id = "CS0003", Severity = "warning", File = "a.cs", Line = 1 },
            };

            // Act
            var result = InvokePrivate<List<DiagnosticDetail>>("OrderDiagnostics", diagnostics, 100);

            // Assert
            result[0].Severity.ShouldBe("error");
            result[1].Severity.ShouldBe("warning");
            result[2].Severity.ShouldBe("info");
        }

        [Fact]
        public void Should_Limit_Results_By_MaxDiagnostics()
        {
            // Arrange
            var diagnostics = new List<DiagnosticDetail>
            {
                new() { Id = "CS0001", Severity = "warning", File = "a.cs", Line = 1 },
                new() { Id = "CS0002", Severity = "warning", File = "a.cs", Line = 2 },
                new() { Id = "CS0003", Severity = "warning", File = "a.cs", Line = 3 },
            };

            // Act
            var result = InvokePrivate<List<DiagnosticDetail>>("OrderDiagnostics", diagnostics, 2);

            // Assert
            result.Count.ShouldBe(2);
        }

        [Fact]
        public void Should_Sort_By_File_Then_Line_Within_Same_Severity()
        {
            // Arrange
            var diagnostics = new List<DiagnosticDetail>
            {
                new() { Id = "CS0001", Severity = "warning", File = "z.cs", Line = 1 },
                new() { Id = "CS0002", Severity = "warning", File = "a.cs", Line = 5 },
                new() { Id = "CS0003", Severity = "warning", File = "a.cs", Line = 1 },
            };

            // Act
            var result = InvokePrivate<List<DiagnosticDetail>>("OrderDiagnostics", diagnostics, 100);

            // Assert - same severity: order by file then line
            result[0].File.ShouldBe("a.cs");
            result[0].Line.ShouldBe(1);
            result[1].File.ShouldBe("a.cs");
            result[1].Line.ShouldBe(5);
            result[2].File.ShouldBe("z.cs");
        }

        [Fact]
        public void Should_Return_Empty_For_Empty_Input()
        {
            // Arrange
            var diagnostics = new List<DiagnosticDetail>();

            // Act
            var result = InvokePrivate<List<DiagnosticDetail>>("OrderDiagnostics", diagnostics, 100);

            // Assert
            result.ShouldBeEmpty();
        }
    }

    #endregion

    #region UpdateIdStatistics

    public class UpdateIdStatisticsTests : SolutionAnalyzerServiceInternalTests
    {
        [Fact]
        public void Should_Initialize_And_Increment_New_Id()
        {
            // Arrange
            var byId = new Dictionary<string, int>();

            // Act
            InvokePrivateVoid("UpdateIdStatistics", byId, "CS0168");

            // Assert
            byId["CS0168"].ShouldBe(1);
        }

        [Fact]
        public void Should_Increment_Existing_Id()
        {
            // Arrange
            var byId = new Dictionary<string, int> { { "CS0168", 2 } };

            // Act
            InvokePrivateVoid("UpdateIdStatistics", byId, "CS0168");

            // Assert
            byId["CS0168"].ShouldBe(3);
        }

        [Fact]
        public void Should_Track_Multiple_Ids_Independently()
        {
            // Arrange
            var byId = new Dictionary<string, int>();

            // Act
            InvokePrivateVoid("UpdateIdStatistics", byId, "CS0168");
            InvokePrivateVoid("UpdateIdStatistics", byId, "CS0219");
            InvokePrivateVoid("UpdateIdStatistics", byId, "CS0168");

            // Assert
            byId["CS0168"].ShouldBe(2);
            byId["CS0219"].ShouldBe(1);
        }
    }

    #endregion

    #region UpdateSeverityStatistics

    public class UpdateSeverityStatisticsTests : SolutionAnalyzerServiceInternalTests
    {
        [Fact]
        public void Should_Initialize_And_Increment_Error()
        {
            // Arrange
            var bySeverity = new Dictionary<string, int>();

            // Act
            InvokePrivateVoid("UpdateSeverityStatistics", bySeverity, DiagnosticSeverity.Error);

            // Assert
            bySeverity["Error"].ShouldBe(1);
        }

        [Fact]
        public void Should_Increment_Existing_Severity()
        {
            // Arrange
            var bySeverity = new Dictionary<string, int> { { "Warning", 3 } };

            // Act
            InvokePrivateVoid("UpdateSeverityStatistics", bySeverity, DiagnosticSeverity.Warning);

            // Assert
            bySeverity["Warning"].ShouldBe(4);
        }

        [Theory]
        [InlineData(DiagnosticSeverity.Error, "Error")]
        [InlineData(DiagnosticSeverity.Warning, "Warning")]
        [InlineData(DiagnosticSeverity.Info, "Info")]
        [InlineData(DiagnosticSeverity.Hidden, "Hidden")]
        public void Should_Use_Correct_Key_For_Severity(DiagnosticSeverity severity, string expectedKey)
        {
            // Arrange
            var bySeverity = new Dictionary<string, int>();

            // Act
            InvokePrivateVoid("UpdateSeverityStatistics", bySeverity, severity);

            // Assert
            bySeverity.ContainsKey(expectedKey).ShouldBeTrue();
            bySeverity[expectedKey].ShouldBe(1);
        }
    }

    #endregion

    #region CheckFixability

    public class CheckFixabilityTests : SolutionAnalyzerServiceInternalTests
    {
        [Fact]
        public void Should_Add_Fixable_Id_To_Set()
        {
            // Arrange
            var fixableIds = new HashSet<string>();

            // Act
            InvokePrivateVoid("CheckFixability", fixableIds, "CS0168", null); // CS0168 is known fixable

            // Assert
            fixableIds.ShouldContain("CS0168");
        }

        [Fact]
        public void Should_Not_Add_Unknown_Id_To_Set()
        {
            // Arrange
            var fixableIds = new HashSet<string>();

            // Act
            InvokePrivateVoid("CheckFixability", fixableIds, "UNKNOWN999", null);

            // Assert
            fixableIds.ShouldBeEmpty();
        }

        [Fact]
        public void Should_Not_Duplicate_Fixable_Id()
        {
            // Arrange
            var fixableIds = new HashSet<string>();

            // Act
            InvokePrivateVoid("CheckFixability", fixableIds, "CS0168", null);
            InvokePrivateVoid("CheckFixability", fixableIds, "CS0168", null);

            // Assert
            fixableIds.Count.ShouldBe(1);
        }
    }

    #endregion

    #region CreateDiagnosticDetails

    public class CreateDiagnosticDetailsTests : SolutionAnalyzerServiceInternalTests
    {
        [Fact]
        public void Should_Take_Max_Diagnostics()
        {
            // Arrange
            var descriptor = new DiagnosticDescriptor(
                "CS0168", "Test", "Message", "Test",
                DiagnosticSeverity.Warning, isEnabledByDefault: true);
            var diagnostics = Enumerable.Range(0, 10)
                .Select(_ => Diagnostic.Create(descriptor, Location.None))
                .ToList();

            // Act
            var result = InvokePrivate<List<DiagnosticDetail>>("CreateDiagnosticDetails", diagnostics, "MyProject", 5);

            // Assert
            result.Count.ShouldBe(5);
        }

        [Fact]
        public void Should_Return_All_When_Under_Max()
        {
            // Arrange
            var descriptor = new DiagnosticDescriptor(
                "CS0219", "Test", "Message", "Test",
                DiagnosticSeverity.Warning, isEnabledByDefault: true);
            var diagnostics = Enumerable.Range(0, 3)
                .Select(_ => Diagnostic.Create(descriptor, Location.None))
                .ToList();

            // Act
            var result = InvokePrivate<List<DiagnosticDetail>>("CreateDiagnosticDetails", diagnostics, "MyProject", 100);

            // Assert
            result.Count.ShouldBe(3);
            result.ShouldAllBe(d => d.Project == "MyProject");
        }

        [Fact]
        public void Should_Return_Empty_For_Empty_Input()
        {
            // Act
            var result = InvokePrivate<List<DiagnosticDetail>>("CreateDiagnosticDetails",
                new List<Diagnostic>(), "MyProject", 100);

            // Assert
            result.ShouldBeEmpty();
        }
    }

    #endregion
}
