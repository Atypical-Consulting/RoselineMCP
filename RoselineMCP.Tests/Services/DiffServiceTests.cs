using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class DiffServiceTests
{
    private readonly DiffService _sut;

    public DiffServiceTests()
    {
        _sut = new DiffService();
    }

    public class GenerateUnifiedDiffTests : DiffServiceTests
    {
        [Fact]
        public void Should_Return_Empty_When_No_Differences()
        {
            // Arrange
            var text = "Line 1\nLine 2\nLine 3";

            // Act
            var result = _sut.GenerateUnifiedDiff(text, text, "a/file.cs", "b/file.cs");

            // Assert
            result.ShouldBeEmpty();
        }

        [Fact]
        public void Should_Generate_Diff_For_Added_Lines()
        {
            // Arrange
            var oldText = "Line 1\nLine 2";
            var newText = "Line 1\nLine 2\nLine 3";

            // Act
            var result = _sut.GenerateUnifiedDiff(oldText, newText, "a/file.cs", "b/file.cs");

            // Assert
            result.ShouldNotBeNullOrWhiteSpace();
            result.ShouldContain("--- a/file.cs");
            result.ShouldContain("+++ b/file.cs");
            result.ShouldContain("+Line 3");
        }

        [Fact]
        public void Should_Generate_Diff_For_Removed_Lines()
        {
            // Arrange
            var oldText = "Line 1\nLine 2\nLine 3";
            var newText = "Line 1\nLine 3";

            // Act
            var result = _sut.GenerateUnifiedDiff(oldText, newText, "a/file.cs", "b/file.cs");

            // Assert
            result.ShouldNotBeNullOrWhiteSpace();
            result.ShouldContain("-Line 2");
        }

        [Fact]
        public void Should_Include_Context_Lines()
        {
            // Arrange
            var oldText = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5";
            var newText = "Line 1\nLine 2\nModified Line 3\nLine 4\nLine 5";

            // Act
            var result = _sut.GenerateUnifiedDiff(oldText, newText, "a/file.cs", "b/file.cs");

            // Assert
            result.ShouldNotBeNullOrWhiteSpace();
            // Should include context lines around the change
            result.ShouldContain(" Line 2");  // Context before
            result.ShouldContain("-Line 3");  // Removed
            result.ShouldContain("+Modified Line 3");  // Added
            result.ShouldContain(" Line 4");  // Context after
        }

        [Fact]
        public void Should_Generate_Diff_For_Whitespace_Only_Reindentation()
        {
            // Arrange — same tokens, different leading indentation
            var oldText = "void M()\n{\n    DoWork();\n}";
            var newText = "void M()\n{\n        DoWork();\n}";

            // Act
            var result = _sut.GenerateUnifiedDiff(oldText, newText, "a/file.cs", "b/file.cs");

            // Assert — whitespace must never be silently ignored
            result.ShouldNotBeNullOrWhiteSpace();
            result.ShouldContain("-    DoWork();");
            result.ShouldContain("+        DoWork();");
        }

        [Fact]
        public void Should_Generate_Diff_For_Trailing_Whitespace_Only_Change()
        {
            // Arrange
            var oldText = "Line 1   \nLine 2";
            var newText = "Line 1\nLine 2";

            // Act
            var result = _sut.GenerateUnifiedDiff(oldText, newText, "a/file.cs", "b/file.cs");

            // Assert
            result.ShouldNotBeNullOrWhiteSpace();
            result.ShouldContain("-Line 1   ");
            result.ShouldContain("+Line 1");
        }

        [Fact]
        public void Should_Generate_Proper_Hunk_Headers()
        {
            // Arrange
            var oldText = "Line 1";
            var newText = "Line 1\nLine 2";

            // Act
            var result = _sut.GenerateUnifiedDiff(oldText, newText, "a/file.cs", "b/file.cs");

            // Assert
            result.ShouldNotBeNullOrWhiteSpace();
            result.ShouldContain("@@");
            // Hunk header should be in format @@ -oldStart,oldCount +newStart,newCount @@
            result.ShouldMatch(@"@@\s+-\d+,\d+\s+\+\d+,\d+\s+@@");
        }
    }

    public class NormalizeWhitespaceTests : DiffServiceTests
    {
        [Fact]
        public void Should_Trim_Trailing_Whitespace()
        {
            // Arrange
            var text = "Line 1  \nLine 2    \nLine 3 ";

            // Act
            var result = _sut.NormalizeWhitespace(text);

            // Assert
            result.ShouldBe("Line 1\nLine 2\nLine 3");
        }

        [Fact]
        public void Should_Collapse_Multiple_Spaces()
        {
            // Arrange
            var text = "Line   with    multiple     spaces";

            // Act
            var result = _sut.NormalizeWhitespace(text);

            // Assert
            result.ShouldBe("Line with multiple spaces");
        }

        [Fact]
        public void Should_Handle_Mixed_Whitespace()
        {
            // Arrange
            var text = "Line 1  \t  \nLine   2\t\t\nLine\t3  ";

            // Act
            var result = _sut.NormalizeWhitespace(text);

            // Assert
            result.ShouldBe("Line 1\nLine 2\nLine 3");
        }

        [Fact]
        public void Should_Handle_Empty_String()
        {
            // Arrange
            var text = "";

            // Act
            var result = _sut.NormalizeWhitespace(text);

            // Assert
            result.ShouldBe("");
        }

        [Fact]
        public void Should_Handle_Only_Whitespace()
        {
            // Arrange
            var text = "   \t  \n  \t ";

            // Act
            var result = _sut.NormalizeWhitespace(text);

            // Assert
            result.ShouldBe("\n");
        }
    }
}