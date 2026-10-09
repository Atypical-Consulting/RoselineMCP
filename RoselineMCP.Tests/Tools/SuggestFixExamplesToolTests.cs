using FakeItEasy;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Tools;
using Shouldly;

namespace RoselineMCP.Tests.Tools;

public class SuggestFixExamplesToolTests
{
    private readonly IFixExampleService _service = A.Fake<IFixExampleService>();

    [Fact]
    public async Task Returns_Success_Envelope()
    {
        A.CallTo(() => _service.SuggestAsync(A<string?>._, A<string>._, A<string?>._, A<int?>._, A<int>._, A<int>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new FixExamplesResponse
            {
                ResolvedPath = "/x/App.sln",
                Rule = new RuleInfo { Id = "CS0121" },
                Examples = [new FixExample { File = "A.cs", Line = 1 }],
            }));

        var result = await SuggestFixExamplesTool.SuggestFixExamples(_service, "CS0121", project: "Demo");

        result.Ok.ShouldBeTrue();
        result.Data!.Examples.ShouldHaveSingleItem().File.ShouldBe("A.cs");
    }

    [Fact]
    public async Task Service_Failure_Becomes_A_Classified_Envelope_Not_An_Exception()
    {
        A.CallTo(() => _service.SuggestAsync(A<string?>._, A<string>._, A<string?>._, A<int?>._, A<int>._, A<int>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("boom"));

        var result = await SuggestFixExamplesTool.SuggestFixExamples(_service, "CS0121");

        result.Ok.ShouldBeFalse();
        result.Error!.Type.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Cancellation_Becomes_A_Failure_Envelope()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        A.CallTo(() => _service.SuggestAsync(A<string?>._, A<string>._, A<string?>._, A<int?>._, A<int>._, A<int>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException(cts.Token));

        var result = await SuggestFixExamplesTool.SuggestFixExamples(_service, "CS0121", cancellationToken: cts.Token);

        result.Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task Empty_Id_Is_A_ValidationError_Without_ResolvedPath_And_Never_Loads()
    {
        var result = await SuggestFixExamplesTool.SuggestFixExamples(_service, "  ");

        result.Ok.ShouldBeFalse();
        result.Error!.Type.ShouldBe("ValidationError");
        result.Error.ResolvedPath.ShouldBeNull();
        A.CallTo(_service).MustNotHaveHappened();
    }
}
