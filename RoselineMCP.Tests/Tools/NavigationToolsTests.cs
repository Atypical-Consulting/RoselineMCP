using FakeItEasy;
using ModelContextProtocol;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Tools;
using Shouldly;

namespace RoselineMCP.Tests.Tools;

/// <summary>
/// Unit tests for the navigation and edit MCP tools. These invoke the static tool methods directly
/// with a faked service (mirroring <see cref="AnalysisToolsTests"/>), asserting the typed
/// <see cref="ToolResult{T}"/> envelope, argument pass-through, the error contract, and
/// preview-by-default safety.
/// </summary>
public class NavigationToolsTests
{
    public class SearchSymbolsTests
    {
        private readonly ICodeNavigationService _service = A.Fake<ICodeNavigationService>();

        [Fact]
        public async Task Should_Return_Success_Envelope()
        {
            A.CallTo(() => _service.SearchSymbolsAsync(A<string>._, A<string?>._, A<string?>._, A<string[]?>._, A<int>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new SymbolSearchResponse
                {
                    Project = "Demo",
                    Query = "*Service",
                    TotalFound = 1,
                    Symbols = [new SymbolSummary { Name = "UserService", Kind = "class" }]
                }));

            var result = await SearchSymbolsTool.SearchSymbols(_service, "Demo", "*Service");

            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.Query.ShouldBe("*Service");
            result.Data.Symbols.ShouldHaveSingleItem().Name.ShouldBe("UserService");
        }

        [Fact]
        public async Task Should_Return_Validation_Error_When_No_Query_Or_File()
        {
            var result = await SearchSymbolsTool.SearchSymbols(_service, "Demo");

            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("ValidationError");
            result.Error.Message.ShouldContain("query");

            A.CallTo(() => _service.SearchSymbolsAsync(A<string>._, A<string?>._, A<string?>._, A<string[]?>._, A<int>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }

        [Fact]
        public async Task Should_Pass_All_Parameters_To_Service()
        {
            var kinds = new[] { "class", "method" };
            A.CallTo(() => _service.SearchSymbolsAsync("Demo", "User", "User.cs", kinds, 25, A<CancellationToken>._))
                .Returns(Task.FromResult(new SymbolSearchResponse()));

            await SearchSymbolsTool.SearchSymbols(_service, "Demo", "User", "User.cs", kinds, 25);

            A.CallTo(() => _service.SearchSymbolsAsync("Demo", "User", "User.cs", kinds, 25, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Fact]
        public async Task Should_Return_NotFound_Error_When_Service_Throws_KeyNotFound()
        {
            A.CallTo(() => _service.SearchSymbolsAsync(A<string>._, A<string?>._, A<string?>._, A<string[]?>._, A<int>._, A<CancellationToken>._))
                .Throws(new KeyNotFoundException("File not found in project"));

            var result = await SearchSymbolsTool.SearchSymbols(_service, "Demo", file: "Missing.cs");

            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("NotFoundError");
            result.Error.Message.ShouldContain("File not found");
        }
    }

    public class GetSymbolInfoTests
    {
        private readonly ICodeNavigationService _service = A.Fake<ICodeNavigationService>();

        [Fact]
        public async Task Should_Return_Success_Envelope()
        {
            A.CallTo(() => _service.GetSymbolInfoAsync(A<string>._, A<string>._, A<bool>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new SymbolInfoResponse
                {
                    Name = "GetUser",
                    FullName = "Acme.UserService.GetUser",
                    Kind = "method"
                }));

            var result = await GetSymbolInfoTool.GetSymbolInfo(_service, "Demo", "GetUser");

            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.FullName.ShouldBe("Acme.UserService.GetUser");
        }

        [Fact]
        public async Task Should_Default_IncludeSource_To_True()
        {
            bool? captured = null;
            A.CallTo(() => _service.GetSymbolInfoAsync(A<string>._, A<string>._, A<bool>._, A<CancellationToken>._))
                .Invokes((string _, string _, bool includeSource, CancellationToken _) => captured = includeSource)
                .Returns(Task.FromResult(new SymbolInfoResponse()));

            await GetSymbolInfoTool.GetSymbolInfo(_service, "Demo", "GetUser");

            captured.ShouldBe(true);
        }

        [Fact]
        public async Task Should_Return_Validation_Error_When_Service_Throws_Ambiguous()
        {
            A.CallTo(() => _service.GetSymbolInfoAsync(A<string>._, A<string>._, A<bool>._, A<CancellationToken>._))
                .Throws(new ArgumentException("Ambiguous symbol 'Get'"));

            var result = await GetSymbolInfoTool.GetSymbolInfo(_service, "Demo", "Get");

            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("ValidationError");
            result.Error.Message.ShouldContain("Ambiguous");
        }
    }

    public class FindReferencesTests
    {
        private readonly ICodeNavigationService _service = A.Fake<ICodeNavigationService>();

        [Fact]
        public async Task Should_Return_Success_Envelope()
        {
            A.CallTo(() => _service.FindReferencesAsync(A<string>._, A<string>._, A<bool>._, A<int>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new ReferencesResponse
                {
                    Symbol = "GetUser",
                    TotalReferences = 2,
                    References = [new ReferenceLocation { File = "A.cs", Line = 3 }]
                }));

            var result = await FindReferencesTool.FindReferences(_service, "Demo", "GetUser");

            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.TotalReferences.ShouldBe(2);
        }

        [Fact]
        public async Task Should_Default_IncludeDefinition_To_False()
        {
            bool? captured = null;
            A.CallTo(() => _service.FindReferencesAsync(A<string>._, A<string>._, A<bool>._, A<int>._, A<CancellationToken>._))
                .Invokes((string _, string _, bool includeDefinition, int _, CancellationToken _) => captured = includeDefinition)
                .Returns(Task.FromResult(new ReferencesResponse()));

            await FindReferencesTool.FindReferences(_service, "Demo", "GetUser");

            captured.ShouldBe(false);
        }
    }

    public class FindImplementationsTests
    {
        private readonly ICodeNavigationService _service = A.Fake<ICodeNavigationService>();

        [Fact]
        public async Task Should_Return_Success_Envelope()
        {
            A.CallTo(() => _service.FindImplementationsAsync(A<string>._, A<string>._, A<int>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new ImplementationsResponse
                {
                    Symbol = "IRepository",
                    Kind = "interface",
                    TotalFound = 1,
                    Implementations = [new SymbolSummary { Name = "SqlRepository", Kind = "class" }]
                }));

            var result = await FindImplementationsTool.FindImplementations(_service, "Demo", "IRepository");

            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.Implementations.ShouldHaveSingleItem().Name.ShouldBe("SqlRepository");
        }
    }

    public class GetCallGraphTests
    {
        private readonly ICodeNavigationService _service = A.Fake<ICodeNavigationService>();

        [Fact]
        public async Task Should_Return_Success_Envelope()
        {
            A.CallTo(() => _service.GetCallGraphAsync(A<string>._, A<string>._, A<string>._, A<int>._, A<int>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new CallGraphResponse
                {
                    Method = "Handle",
                    Direction = "callers",
                    Depth = 1,
                    Callers = [new CallGraphNode { FullName = "Controller.Post" }]
                }));

            var result = await GetCallGraphTool.GetCallGraph(_service, "Demo", "Handle");

            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.Callers.ShouldNotBeNull();
            result.Data.Callers.ShouldHaveSingleItem().FullName.ShouldBe("Controller.Post");
        }

        [Fact]
        public async Task Should_Default_Direction_And_Depth()
        {
            string? direction = null;
            int? depth = null;
            A.CallTo(() => _service.GetCallGraphAsync(A<string>._, A<string>._, A<string>._, A<int>._, A<int>._, A<CancellationToken>._))
                .Invokes((string _, string _, string dir, int d, int _, CancellationToken _) => { direction = dir; depth = d; })
                .Returns(Task.FromResult(new CallGraphResponse()));

            await GetCallGraphTool.GetCallGraph(_service, "Demo", "Handle");

            direction.ShouldBe("callers");
            depth.ShouldBe(1);
        }
    }

    public class GetTypeHierarchyTests
    {
        private readonly ICodeNavigationService _service = A.Fake<ICodeNavigationService>();

        [Fact]
        public async Task Should_Return_Success_Envelope()
        {
            A.CallTo(() => _service.GetTypeHierarchyAsync(A<string>._, A<string>._, A<string>._, A<int>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new TypeHierarchyResponse
                {
                    Type = "SqlRepository",
                    Direction = "both",
                    BaseTypes = [new SymbolSummary { Name = "RepositoryBase" }]
                }));

            var result = await GetTypeHierarchyTool.GetTypeHierarchy(_service, "Demo", "SqlRepository");

            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.BaseTypes.ShouldNotBeNull();
            result.Data.BaseTypes.ShouldHaveSingleItem().Name.ShouldBe("RepositoryBase");
        }
    }

    public class EditMemberTests
    {
        private readonly ICodeEditService _service = A.Fake<ICodeEditService>();

        [Fact]
        public async Task Should_Return_Validation_Error_For_Invalid_Operation()
        {
            var result = await EditMemberTool.EditMember(_service, "Demo", "Foo.Bar", "frobnicate");

            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("ValidationError");
            result.Error.Hint.ShouldNotBeNull();
            result.Error.Hint.ShouldContain("replace, add, delete");

            A.CallTo(() => _service.EditMemberAsync(A<string>._, A<string>._, A<string>._, A<string?>._, A<bool>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }

        [Fact]
        public async Task Should_Default_To_PreviewOnly_True()
        {
            bool? captured = null;
            A.CallTo(() => _service.EditMemberAsync(A<string>._, A<string>._, A<string>._, A<string?>._, A<bool>._, A<CancellationToken>._))
                .Invokes((string _, string _, string _, string? _, bool previewOnly, CancellationToken _) => captured = previewOnly)
                .Returns(Task.FromResult(new EditMemberResponse()));

            await EditMemberTool.EditMember(_service, "Demo", "Foo.Bar", "delete");

            captured.ShouldBe(true);
        }

        [Fact]
        public async Task Should_Pass_NewSource_And_PreviewOnly_False()
        {
            string? capturedSource = null;
            bool? capturedPreview = null;
            A.CallTo(() => _service.EditMemberAsync(A<string>._, A<string>._, A<string>._, A<string?>._, A<bool>._, A<CancellationToken>._))
                .Invokes((string _, string _, string _, string? src, bool preview, CancellationToken _) => { capturedSource = src; capturedPreview = preview; })
                .Returns(Task.FromResult(new EditMemberResponse { Applied = true }));

            await EditMemberTool.EditMember(_service, "Demo", "Foo", "add", "public int X => 1;", previewOnly: false);

            capturedSource.ShouldBe("public int X => 1;");
            capturedPreview.ShouldBe(false);
        }
    }

    public class RenameSymbolTests
    {
        private readonly ICodeEditService _service = A.Fake<ICodeEditService>();

        [Fact]
        public async Task Should_Return_Validation_Error_When_NewName_Missing()
        {
            var result = await RenameSymbolTool.RenameSymbol(_service, "Demo", "Foo.Bar", "");

            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("ValidationError");

            A.CallTo(() => _service.RenameSymbolAsync(A<string>._, A<string>._, A<string>._, A<bool>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }

        [Fact]
        public async Task Should_Default_To_PreviewOnly_True()
        {
            bool? captured = null;
            A.CallTo(() => _service.RenameSymbolAsync(A<string>._, A<string>._, A<string>._, A<bool>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
                .Invokes((string _, string _, string _, bool previewOnly, IProgress<ProgressNotificationValue>? _, CancellationToken _) => captured = previewOnly)
                .Returns(Task.FromResult(new RenameSymbolResponse()));

            await RenameSymbolTool.RenameSymbol(_service, "Demo", "Foo.Bar", "Baz");

            captured.ShouldBe(true);
        }

        [Fact]
        public async Task Should_Return_Success_Envelope()
        {
            A.CallTo(() => _service.RenameSymbolAsync(A<string>._, A<string>._, A<string>._, A<bool>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new RenameSymbolResponse
                {
                    Symbol = "Acme.Foo.Bar",
                    NewName = "Baz",
                    ChangedFiles = ["Foo.cs"],
                    Patch = "--- a/Foo.cs"
                }));

            var result = await RenameSymbolTool.RenameSymbol(_service, "Demo", "Bar", "Baz");

            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.NewName.ShouldBe("Baz");
            result.Data.ChangedFiles.ShouldHaveSingleItem().ShouldBe("Foo.cs");
        }
    }
}
