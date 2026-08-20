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

            var result = await SearchSymbolsTool.SearchSymbols(_service, "*Service", project: "Demo");

            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.Query.ShouldBe("*Service");
            result.Data.Symbols.ShouldHaveSingleItem().Name.ShouldBe("UserService");
        }

        [Fact]
        public async Task Should_Return_Validation_Error_When_No_Query_Or_File()
        {
            var result = await SearchSymbolsTool.SearchSymbols(_service, project: "Demo");

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

            await SearchSymbolsTool.SearchSymbols(_service, "User", "User.cs", kinds, 25, "Demo");

            A.CallTo(() => _service.SearchSymbolsAsync("Demo", "User", "User.cs", kinds, 25, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Fact]
        public async Task Should_Return_NotFound_Error_When_Service_Throws_KeyNotFound()
        {
            A.CallTo(() => _service.SearchSymbolsAsync(A<string>._, A<string?>._, A<string?>._, A<string[]?>._, A<int>._, A<CancellationToken>._))
                .Throws(new KeyNotFoundException("File not found in project"));

            var result = await SearchSymbolsTool.SearchSymbols(_service, file: "Missing.cs", project: "Demo");

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

            var result = await GetSymbolInfoTool.GetSymbolInfo(_service, "GetUser", project: "Demo");

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

            await GetSymbolInfoTool.GetSymbolInfo(_service, "GetUser", project: "Demo");

            captured.ShouldBe(true);
        }

        [Fact]
        public async Task Should_Return_Validation_Error_When_Service_Throws_Ambiguous()
        {
            A.CallTo(() => _service.GetSymbolInfoAsync(A<string>._, A<string>._, A<bool>._, A<CancellationToken>._))
                .Throws(new ArgumentException("Ambiguous symbol 'Get'"));

            var result = await GetSymbolInfoTool.GetSymbolInfo(_service, "Get", project: "Demo");

            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("ValidationError");
            result.Error.Message.ShouldContain("Ambiguous");
        }
    }

    public class GetSymbolAtPositionTests
    {
        private readonly ICodeNavigationService _service = A.Fake<ICodeNavigationService>();

        [Fact]
        public async Task Should_Return_Success_Envelope()
        {
            A.CallTo(() => _service.GetSymbolAtPositionAsync(A<string>._, A<string>._, A<int>._, A<int?>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new SymbolAtPositionResponse
                {
                    Name = "Deposit",
                    FullName = "Acme.Account.Deposit",
                    Kind = "method",
                    IsDeclaration = true
                }));

            var result = await GetSymbolAtPositionTool.GetSymbolAtPosition(_service, "Account.cs", 3, project: "Demo");

            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.FullName.ShouldBe("Acme.Account.Deposit");
            result.Data.IsDeclaration.ShouldBeTrue();
        }

        [Fact]
        public async Task Should_Return_Validation_Error_When_File_Missing()
        {
            var result = await GetSymbolAtPositionTool.GetSymbolAtPosition(_service, "", 3, project: "Demo");

            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("ValidationError");
            result.Error.Message.ShouldContain("file");

            A.CallTo(() => _service.GetSymbolAtPositionAsync(A<string>._, A<string>._, A<int>._, A<int?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }

        [Fact]
        public async Task Should_Return_Validation_Error_For_NonPositive_Line()
        {
            var result = await GetSymbolAtPositionTool.GetSymbolAtPosition(_service, "Account.cs", 0, project: "Demo");

            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("ValidationError");
            result.Error.Message.ShouldContain("1-based");

            A.CallTo(() => _service.GetSymbolAtPositionAsync(A<string>._, A<string>._, A<int>._, A<int?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }

        [Fact]
        public async Task Should_Pass_All_Parameters_To_Service()
        {
            A.CallTo(() => _service.GetSymbolAtPositionAsync("Demo", "Account.cs", 12, 5, A<CancellationToken>._))
                .Returns(Task.FromResult(new SymbolAtPositionResponse()));

            await GetSymbolAtPositionTool.GetSymbolAtPosition(_service, "Account.cs", 12, 5, "Demo");

            A.CallTo(() => _service.GetSymbolAtPositionAsync("Demo", "Account.cs", 12, 5, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Fact]
        public async Task Should_Return_NotFound_Error_When_Service_Throws_KeyNotFound()
        {
            A.CallTo(() => _service.GetSymbolAtPositionAsync(A<string>._, A<string>._, A<int>._, A<int?>._, A<CancellationToken>._))
                .Throws(new KeyNotFoundException("No symbol found at Account.cs:7"));

            var result = await GetSymbolAtPositionTool.GetSymbolAtPosition(_service, "Account.cs", 7, project: "Demo");

            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("NotFoundError");
            result.Error.Message.ShouldContain("No symbol found");
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

            var result = await FindReferencesTool.FindReferences(_service, "GetUser", project: "Demo");

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

            await FindReferencesTool.FindReferences(_service, "GetUser", project: "Demo");

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

            var result = await FindImplementationsTool.FindImplementations(_service, "IRepository", project: "Demo");

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

            var result = await GetCallGraphTool.GetCallGraph(_service, "Handle", project: "Demo");

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

            await GetCallGraphTool.GetCallGraph(_service, "Handle", project: "Demo");

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

            var result = await GetTypeHierarchyTool.GetTypeHierarchy(_service, "SqlRepository", project: "Demo");

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
            var result = await EditMemberTool.EditMember(_service, "Foo.Bar", "frobnicate", project: "Demo");

            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("ValidationError");
            result.Error.Hint.ShouldNotBeNull();
            result.Error.Hint.ShouldContain("replace, add, delete");

            A.CallTo(() => _service.EditMemberAsync(A<string>._, A<string>._, A<string>._, A<string?>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }

        [Fact]
        public async Task Should_Default_To_PreviewOnly_True()
        {
            bool? captured = null;
            A.CallTo(() => _service.EditMemberAsync(A<string>._, A<string>._, A<string>._, A<string?>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
                .Invokes((string _, string _, string _, string? _, bool previewOnly, bool _, int _, CancellationToken _) => captured = previewOnly)
                .Returns(Task.FromResult(new EditMemberResponse()));

            await EditMemberTool.EditMember(_service, "Foo.Bar", "delete", project: "Demo");

            captured.ShouldBe(true);
        }

        [Fact]
        public async Task Should_Pass_NewSource_And_PreviewOnly_False()
        {
            string? capturedSource = null;
            bool? capturedPreview = null;
            A.CallTo(() => _service.EditMemberAsync(A<string>._, A<string>._, A<string>._, A<string?>._, A<bool>._, A<bool>._, A<int>._, A<CancellationToken>._))
                .Invokes((string _, string _, string _, string? src, bool preview, bool _, int _, CancellationToken _) => { capturedSource = src; capturedPreview = preview; })
                .Returns(Task.FromResult(new EditMemberResponse { Applied = true }));

            await EditMemberTool.EditMember(_service, "Foo", "add", "public int X => 1;", previewOnly: false, project: "Demo");

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
            var result = await RenameSymbolTool.RenameSymbol(_service, "Foo.Bar", "", project: "Demo");

            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("ValidationError");

            A.CallTo(() => _service.RenameSymbolAsync(A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }

        [Fact]
        public async Task Should_Default_To_PreviewOnly_True()
        {
            bool? captured = null;
            A.CallTo(() => _service.RenameSymbolAsync(A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
                .Invokes((string _, string _, string _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) => captured = previewOnly)
                .Returns(Task.FromResult(new RenameSymbolResponse()));

            await RenameSymbolTool.RenameSymbol(_service, "Foo.Bar", "Baz", project: "Demo");

            captured.ShouldBe(true);
        }

        [Fact]
        public async Task Should_Return_Success_Envelope()
        {
            A.CallTo(() => _service.RenameSymbolAsync(A<string>._, A<string>._, A<string>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new RenameSymbolResponse
                {
                    Symbol = "Acme.Foo.Bar",
                    NewName = "Baz",
                    ChangedFiles = ["Foo.cs"],
                    Patch = "--- a/Foo.cs"
                }));

            var result = await RenameSymbolTool.RenameSymbol(_service, "Bar", "Baz", project: "Demo");

            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.NewName.ShouldBe("Baz");
            result.Data.ChangedFiles.ShouldHaveSingleItem().ShouldBe("Foo.cs");
        }
    }
}
