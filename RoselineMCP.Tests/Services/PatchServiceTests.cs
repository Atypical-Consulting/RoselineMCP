using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using RoselineMCP.Interfaces;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class PatchServiceTests
{
    private readonly ILogger<PatchService> _logger;
    private readonly IDiffService _diffService;
    private readonly PatchService _sut;

    public PatchServiceTests()
    {
        _logger = A.Fake<ILogger<PatchService>>();
        _diffService = A.Fake<IDiffService>();
        _sut = new PatchService(_logger, _diffService);
    }

    public class CreatePatchTests : PatchServiceTests
    {
        [Fact]
        public void Should_Return_No_Changes_When_Texts_Are_Identical()
        {
            // Arrange
            var text = "Hello World\nThis is a test";
            A.CallTo(() => _diffService.GenerateUnifiedDiff(text, text, A<string>._, A<string>._))
                .Returns(string.Empty);

            // Act
            var result = _sut.CreatePatch(text, text);

            // Assert
            result.HasChanges.ShouldBeFalse();
            result.Summary.ShouldBe("No changes detected");
            result.LinesAdded.ShouldBe(0);
            result.LinesRemoved.ShouldBe(0);
        }

        [Fact]
        public void Should_Detect_Added_Lines()
        {
            // Arrange
            var before = "Line 1\nLine 2";
            var after = "Line 1\nLine 2\nLine 3";
            var mockPatch = "--- a/file.txt\n+++ b/file.txt\n@@ -1,2 +1,3 @@\n Line 1\n Line 2\n+Line 3";
            A.CallTo(() => _diffService.GenerateUnifiedDiff(before, after, A<string>._, A<string>._))
                .Returns(mockPatch);

            // Act
            var result = _sut.CreatePatch(before, after);

            // Assert
            result.HasChanges.ShouldBeTrue();
            result.LinesAdded.ShouldBe(1);
            result.LinesRemoved.ShouldBe(0);
            result.Patch.ShouldContain("+Line 3");
        }

        [Fact]
        public void Should_Detect_Removed_Lines()
        {
            // Arrange
            var before = "Line 1\nLine 2\nLine 3";
            var after = "Line 1\nLine 3";
            var mockPatch = "--- a/file.txt\n+++ b/file.txt\n@@ -1,3 +1,2 @@\n Line 1\n-Line 2\n Line 3";
            A.CallTo(() => _diffService.GenerateUnifiedDiff(before, after, A<string>._, A<string>._))
                .Returns(mockPatch);

            // Act
            var result = _sut.CreatePatch(before, after);

            // Assert
            result.HasChanges.ShouldBeTrue();
            result.LinesAdded.ShouldBe(0);
            result.LinesRemoved.ShouldBe(1);
            result.Patch.ShouldContain("-Line 2");
        }

        [Fact]
        public void Should_Detect_Modified_Lines()
        {
            // Arrange
            var before = "Line 1\nLine 2\nLine 3";
            var after = "Line 1\nLine 2 Modified\nLine 3";
            var mockPatch = "--- a/file.txt\n+++ b/file.txt\n@@ -1,3 +1,3 @@\n Line 1\n-Line 2\n+Line 2 Modified\n Line 3";
            A.CallTo(() => _diffService.GenerateUnifiedDiff(before, after, A<string>._, A<string>._))
                .Returns(mockPatch);

            // Act
            var result = _sut.CreatePatch(before, after);

            // Assert
            result.HasChanges.ShouldBeTrue();
            result.Patch.ShouldContain("-Line 2");
            result.Patch.ShouldContain("+Line 2 Modified");
        }

        [Fact]
        public void Should_Use_Custom_FileName()
        {
            // Arrange
            var before = "Old content";
            var after = "New content";
            var fileName = "test.cs";
            var mockPatch = $"--- a/{fileName}\n+++ b/{fileName}\n@@ -1 +1 @@\n-Old content\n+New content";
            A.CallTo(() => _diffService.GenerateUnifiedDiff(before, after, $"a/{fileName}", $"b/{fileName}"))
                .Returns(mockPatch);

            // Act
            var result = _sut.CreatePatch(before, after, fileName);

            // Assert
            result.FileName.ShouldBe(fileName);
            result.Patch.ShouldContain($"--- a/{fileName}");
            result.Patch.ShouldContain($"+++ b/{fileName}");
        }

        [Fact]
        public void Should_Generate_Proper_Unified_Diff_Header()
        {
            // Arrange
            var before = "Line 1";
            var after = "Line 1\nLine 2";
            var mockPatch = "--- a/test.txt\n+++ b/test.txt\n@@ -1 +1,2 @@\n Line 1\n+Line 2";
            A.CallTo(() => _diffService.GenerateUnifiedDiff(before, after, "a/test.txt", "b/test.txt"))
                .Returns(mockPatch);

            // Act
            var result = _sut.CreatePatch(before, after, "test.txt");

            // Assert
            result.Patch.ShouldContain("--- a/test.txt");
            result.Patch.ShouldContain("+++ b/test.txt");
            result.Patch.ShouldContain("@@");
        }

        [Fact]
        public void Should_Handle_Empty_Before_Text()
        {
            // Arrange
            var before = "";
            var after = "New content\nLine 2";
            var mockPatch = "--- a/file.txt\n+++ b/file.txt\n@@ -0,0 +1,2 @@\n+New content\n+Line 2";
            A.CallTo(() => _diffService.GenerateUnifiedDiff(before, after, A<string>._, A<string>._))
                .Returns(mockPatch);

            // Act
            var result = _sut.CreatePatch(before, after);

            // Assert
            result.HasChanges.ShouldBeTrue();
            result.LinesAdded.ShouldBe(2);
            result.LinesRemoved.ShouldBe(0);
        }

        [Fact]
        public void Should_Handle_Empty_After_Text()
        {
            // Arrange
            var before = "Old content\nLine 2";
            var after = "";
            var mockPatch = "--- a/file.txt\n+++ b/file.txt\n@@ -1,2 +0,0 @@\n-Old content\n-Line 2";
            A.CallTo(() => _diffService.GenerateUnifiedDiff(before, after, A<string>._, A<string>._))
                .Returns(mockPatch);

            // Act
            var result = _sut.CreatePatch(before, after);

            // Assert
            result.HasChanges.ShouldBeTrue();
            result.LinesAdded.ShouldBe(0);
            result.LinesRemoved.ShouldBe(2);
        }

        [Fact]
        public void Should_Generate_Summary_With_Changes()
        {
            // Arrange
            var before = "Line 1\nLine 2";
            var after = "Line 1\nLine 2 Modified\nLine 3";
            var mockPatch = "--- a/test.cs\n+++ b/test.cs\n@@ -1,2 +1,3 @@\n Line 1\n-Line 2\n+Line 2 Modified\n+Line 3";
            A.CallTo(() => _diffService.GenerateUnifiedDiff(before, after, "a/test.cs", "b/test.cs"))
                .Returns(mockPatch);

            // Act
            var result = _sut.CreatePatch(before, after, "test.cs");

            // Assert
            result.Summary.ShouldContain("test.cs");
            result.Summary.ShouldContain("+");
            result.Summary.ShouldContain("lines");
        }

        [Fact]
        public void Should_Handle_Large_Text_Differences()
        {
            // Arrange
            var before = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"Line {i}"));
            var after = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"Line {i}")) + "\n" +
                       string.Join("\n", Enumerable.Range(51, 50).Select(i => $"Modified Line {i}"));
            var mockPatch = "--- a/file.txt\n+++ b/file.txt\n@@ large diff @@";
            A.CallTo(() => _diffService.GenerateUnifiedDiff(before, after, A<string>._, A<string>._))
                .Returns(mockPatch);

            // Act
            var result = _sut.CreatePatch(before, after);

            // Assert
            result.HasChanges.ShouldBeTrue();
            result.Patch.ShouldNotBeNullOrWhiteSpace();
        }
    }

    public class CreatePatchWithOptionsTests : PatchServiceTests
    {
        [Fact]
        public void Should_Ignore_Whitespace_When_Option_Enabled()
        {
            // Arrange
            var before = "Line 1  \nLine 2    ";
            var after = "Line 1\nLine 2";
            A.CallTo(() => _diffService.NormalizeWhitespace(before))
                .Returns("Line 1\nLine 2");
            A.CallTo(() => _diffService.NormalizeWhitespace(after))
                .Returns("Line 1\nLine 2");
            A.CallTo(() => _diffService.GenerateUnifiedDiff("Line 1\nLine 2", "Line 1\nLine 2", A<string>._, A<string>._))
                .Returns(string.Empty);

            // Act
            var result = _sut.CreatePatchWithOptions(before, after, ignoreWhitespace: true);

            // Assert
            result.HasChanges.ShouldBeFalse();
            result.Summary.ShouldContain("ignore-whitespace");
        }

        [Fact]
        public void Should_Ignore_Case_When_Option_Enabled()
        {
            // Arrange
            var before = "HELLO WORLD";
            var after = "hello world";
            // When ignoreCase is true, both are converted to lowercase
            A.CallTo(() => _diffService.GenerateUnifiedDiff("hello world", "hello world", A<string>._, A<string>._))
                .Returns(string.Empty);

            // Act
            var result = _sut.CreatePatchWithOptions(before, after, ignoreCase: true);

            // Assert
            result.HasChanges.ShouldBeFalse();
            result.Summary.ShouldContain("ignore-case");
        }

        [Fact]
        public void Should_Apply_Multiple_Options()
        {
            // Arrange
            var before = "HELLO   WORLD  ";
            var after = "hello world";
            A.CallTo(() => _diffService.NormalizeWhitespace(before))
                .Returns("HELLO WORLD");
            A.CallTo(() => _diffService.NormalizeWhitespace(after))
                .Returns("hello world");
            // After normalization and lowercase conversion
            A.CallTo(() => _diffService.GenerateUnifiedDiff("hello world", "hello world", A<string>._, A<string>._))
                .Returns(string.Empty);

            // Act
            var result = _sut.CreatePatchWithOptions(before, after, 
                ignoreWhitespace: true, 
                ignoreCase: true);

            // Assert
            result.HasChanges.ShouldBeFalse();
            result.Summary.ShouldContain("ignore-whitespace");
            result.Summary.ShouldContain("ignore-case");
        }

        [Fact]
        public void Should_Use_Default_Context_Lines()
        {
            // Arrange
            var before = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5";
            var after = "Line 1\nLine 2\nModified 3\nLine 4\nLine 5";
            var mockPatch = "--- a/file.txt\n+++ b/file.txt\n@@ -1,5 +1,5 @@\n Line 1\n Line 2\n-Line 3\n+Modified 3\n Line 4\n Line 5";
            A.CallTo(() => _diffService.GenerateUnifiedDiff(before, after, A<string>._, A<string>._))
                .Returns(mockPatch);

            // Act
            var result = _sut.CreatePatchWithOptions(before, after, contextLines: 3);

            // Assert
            result.HasChanges.ShouldBeTrue();
            result.Patch.ShouldNotBeNullOrWhiteSpace();
        }

        [Fact]
        public void Should_Normalize_Multiple_Spaces()
        {
            // Arrange
            var before = "Word1    Word2     Word3";
            var after = "Word1 Word2 Word3";

            // Act
            var result = _sut.CreatePatchWithOptions(before, after, ignoreWhitespace: true);

            // Assert
            result.HasChanges.ShouldBeFalse();
        }
    }

    /// <summary>
    /// Proves that a pre-cancelled token is actually honored rather than silently ignored —
    /// deterministic in-process alternative to timing a real timeout.
    /// </summary>
    public class CancellationTests : PatchServiceTests
    {
        [Fact]
        public void CreatePatch_Should_Throw_When_Token_Already_Cancelled()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            Should.Throw<OperationCanceledException>(() =>
                _sut.CreatePatch("before", "after", cancellationToken: cts.Token));

            A.CallTo(() => _diffService.GenerateUnifiedDiff(
                A<string>._, A<string>._, A<string>._, A<string>._))
                .MustNotHaveHappened();
        }

        [Fact]
        public void CreatePatchWithOptions_Should_Throw_When_Token_Already_Cancelled()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            Should.Throw<OperationCanceledException>(() =>
                _sut.CreatePatchWithOptions("before", "after", cancellationToken: cts.Token));

            A.CallTo(() => _diffService.GenerateUnifiedDiff(
                A<string>._, A<string>._, A<string>._, A<string>._))
                .MustNotHaveHappened();
        }
    }
}