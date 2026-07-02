using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for PatchService exception handling paths (catch blocks).
/// </summary>
public class PatchServiceExceptionTests
{
    private readonly PatchService _sut;
    private readonly IDiffService _diffService;
    private readonly string _testDirectory;

    public PatchServiceExceptionTests()
    {
        var logger = A.Fake<ILogger<PatchService>>();
        _diffService = A.Fake<IDiffService>();
        _sut = new PatchService(logger, _diffService);

        _testDirectory = Path.Combine(Path.GetTempPath(), $"PatchSvcTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    public class CreatePatchExceptionTests : PatchServiceExceptionTests, IDisposable
    {
        [Fact]
        public void Should_Rethrow_When_DiffService_Throws()
        {
            // Arrange — make diff service throw
            A.CallTo(() => _diffService.GenerateUnifiedDiff(
                A<string>._, A<string>._, A<string>._, A<string>._))
                .Throws(new InvalidOperationException("Diff engine exploded"));

            // Act & Assert — exception should propagate
            Should.Throw<InvalidOperationException>(() =>
                _sut.CreatePatch("before", "after", "file.cs"))
                .Message.ShouldBe("Diff engine exploded");
        }

        [Fact]
        public void Should_Rethrow_On_OutOfMemoryException()
        {
            // Arrange
            A.CallTo(() => _diffService.GenerateUnifiedDiff(
                A<string>._, A<string>._, A<string>._, A<string>._))
                .Throws(new OutOfMemoryException("OOM"));

            // Act & Assert
            Should.Throw<OutOfMemoryException>(() =>
                _sut.CreatePatch("before", "after"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_testDirectory, true); } catch { /* ignored */ }
        }
    }

    public class CreatePatchWithOptionsExceptionTests : PatchServiceExceptionTests, IDisposable
    {
        [Fact]
        public void Should_Rethrow_When_Inner_CreatePatch_Throws_Via_DiffService()
        {
            // Arrange
            A.CallTo(() => _diffService.GenerateUnifiedDiff(
                A<string>._, A<string>._, A<string>._, A<string>._))
                .Throws(new ArgumentException("Invalid diff format"));

            // Act & Assert — exception propagates through CreatePatchWithOptions catch + rethrow
            Should.Throw<ArgumentException>(() =>
                _sut.CreatePatchWithOptions("before", "after", "file.cs", contextLines: 5))
                .Message.ShouldBe("Invalid diff format");
        }

        [Fact]
        public void Should_Rethrow_When_NormalizeWhitespace_Throws()
        {
            // Arrange
            A.CallTo(() => _diffService.NormalizeWhitespace(A<string>._))
                .Throws(new InvalidOperationException("Whitespace normalization failed"));

            // Act & Assert
            Should.Throw<InvalidOperationException>(() =>
                _sut.CreatePatchWithOptions("before", "after",
                    ignoreWhitespace: true))
                .Message.ShouldBe("Whitespace normalization failed");
        }

        public void Dispose()
        {
            try { Directory.Delete(_testDirectory, true); } catch { /* ignored */ }
        }
    }

}
