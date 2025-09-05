using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class PatchServiceTests
{
    private readonly ILogger<PatchService> _logger;
    private readonly PatchService _sut;

    public PatchServiceTests()
    {
        _logger = A.Fake<ILogger<PatchService>>();
        _sut = new PatchService(_logger);
    }

    public class CreatePatchTests : PatchServiceTests
    {
        [Fact]
        public void Should_Return_No_Changes_When_Texts_Are_Identical()
        {
            // Arrange
            var text = "Hello World\nThis is a test";

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

            // Act
            var result = _sut.CreatePatch(before, after);

            // Assert
            result.HasChanges.ShouldBeTrue();
            result.Patch.ShouldNotBeNullOrWhiteSpace();
        }
    }

    public class CreatePatchFromFilesTests : PatchServiceTests, IDisposable
    {
        private readonly string _testDirectory;

        public CreatePatchFromFilesTests()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);
        }

        [Fact]
        public void Should_Create_Patch_From_Files()
        {
            // Arrange
            var beforePath = Path.Combine(_testDirectory, "before.txt");
            var afterPath = Path.Combine(_testDirectory, "after.txt");
            File.WriteAllText(beforePath, "Original content");
            File.WriteAllText(afterPath, "Modified content");

            // Act
            var result = _sut.CreatePatchFromFiles(beforePath, afterPath);

            // Assert
            result.HasChanges.ShouldBeTrue();
            result.FileName.ShouldBe("before.txt");
        }

        [Fact]
        public void Should_Throw_When_Before_File_Not_Found()
        {
            // Arrange
            var beforePath = Path.Combine(_testDirectory, "nonexistent.txt");
            var afterPath = Path.Combine(_testDirectory, "after.txt");
            File.WriteAllText(afterPath, "Content");

            // Act & Assert
            Should.Throw<FileNotFoundException>(() => _sut.CreatePatchFromFiles(beforePath, afterPath))
                .Message.ShouldContain("Before file not found");
        }

        [Fact]
        public void Should_Throw_When_After_File_Not_Found()
        {
            // Arrange
            var beforePath = Path.Combine(_testDirectory, "before.txt");
            var afterPath = Path.Combine(_testDirectory, "nonexistent.txt");
            File.WriteAllText(beforePath, "Content");

            // Act & Assert
            Should.Throw<FileNotFoundException>(() => _sut.CreatePatchFromFiles(beforePath, afterPath))
                .Message.ShouldContain("After file not found");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch { }
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

    public class ApplyPatchTests : PatchServiceTests
    {
        [Fact]
        public void Should_Return_False_For_Unimplemented_Apply()
        {
            // Arrange
            var filePath = "test.txt";
            var patch = "--- a/test.txt\n+++ b/test.txt\n@@ -1 +1 @@\n-old\n+new";

            // Act
            var result = _sut.ApplyPatch(filePath, patch);

            // Assert
            result.ShouldBeFalse();
        }
    }
}