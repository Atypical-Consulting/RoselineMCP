using RoselineMCP.Models;
using Shouldly;

namespace RoselineMCP.Tests.Models;

/// <summary>
/// Tests for the DiagnosticDetail model class.
/// </summary>
public class DiagnosticDetailTests
{
    [Fact]
    public void Should_Have_Default_Values()
    {
        // Act
        var detail = new DiagnosticDetail();

        // Assert
        detail.Project.ShouldBe(string.Empty);
        detail.File.ShouldBe(string.Empty);
        detail.Line.ShouldBe(0);
        detail.Column.ShouldBe(0);
        detail.Id.ShouldBe(string.Empty);
        detail.Severity.ShouldBe(string.Empty);
        detail.Message.ShouldBe(string.Empty);
    }

    [Fact]
    public void Should_Set_All_Properties()
    {
        // Act
        var detail = new DiagnosticDetail
        {
            Project = "MyProject",
            File = "src/Program.cs",
            Line = 42,
            Column = 10,
            Id = "CS0168",
            Severity = "warning",
            Message = "Variable 'x' is declared but never used"
        };

        // Assert
        detail.Project.ShouldBe("MyProject");
        detail.File.ShouldBe("src/Program.cs");
        detail.Line.ShouldBe(42);
        detail.Column.ShouldBe(10);
        detail.Id.ShouldBe("CS0168");
        detail.Severity.ShouldBe("warning");
        detail.Message.ShouldBe("Variable 'x' is declared but never used");
    }

    [Fact]
    public void Should_Allow_Modification_Of_Properties()
    {
        // Arrange
        var detail = new DiagnosticDetail { Project = "OldProject" };

        // Act
        detail.Project = "NewProject";

        // Assert
        detail.Project.ShouldBe("NewProject");
    }

    [Fact]
    public void Should_Handle_Large_Line_And_Column_Numbers()
    {
        // Act
        var detail = new DiagnosticDetail
        {
            Line = int.MaxValue,
            Column = int.MaxValue
        };

        // Assert
        detail.Line.ShouldBe(int.MaxValue);
        detail.Column.ShouldBe(int.MaxValue);
    }

    [Fact]
    public void Should_Handle_Unicode_In_Message()
    {
        // Act
        var detail = new DiagnosticDetail
        {
            Message = "Erreur: La variable 'x' n'est pas utilisée — こんにちは"
        };

        // Assert
        detail.Message.ShouldBe("Erreur: La variable 'x' n'est pas utilisée — こんにちは");
    }
}
