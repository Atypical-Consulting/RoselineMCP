using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Integration tests for <see cref="CodeNavigationService"/> that run the real Roslyn navigation
/// logic against in-memory <see cref="Microsoft.CodeAnalysis.AdhocWorkspace"/> projects.
/// </summary>
public class CodeNavigationServiceTests
{
    private static CodeNavigationService CreateService(string projectName, params (string Name, string Code)[] files)
    {
        var (workspace, project) = AdhocProjectBuilder.Create(projectName, files);
        var loader = AdhocProjectBuilder.FakeLoaderFor(workspace, project);
        return new CodeNavigationService(A.Fake<ILogger<CodeNavigationService>>(), loader);
    }

    [Fact]
    public async Task SearchSymbols_Wildcard_Finds_Matching_Types()
    {
        var service = CreateService("Demo", ("Services.cs",
            "public class UserService { } public class OrderService { } public class Helper { }"));

        var result = await service.SearchSymbolsAsync("Demo", "*Service", null, null, 50, CancellationToken.None);

        result.Symbols.Select(s => s.Name).ShouldBe(["OrderService", "UserService"], ignoreOrder: true);
    }

    [Fact]
    public async Task SearchSymbols_Substring_Is_Case_Insensitive()
    {
        var service = CreateService("Demo", ("Services.cs", "public class UserService { }"));

        var result = await service.SearchSymbolsAsync("Demo", "userservice", null, null, 50, CancellationToken.None);

        result.Symbols.ShouldHaveSingleItem().Name.ShouldBe("UserService");
    }

    [Fact]
    public async Task SearchSymbols_File_Outline_Returns_Members()
    {
        var service = CreateService("Demo", ("Models.cs",
            "public class Account { public int Id { get; set; } public void Deposit() { } public void Withdraw() { } }"));

        var result = await service.SearchSymbolsAsync("Demo", null, "Models.cs", null, 50, CancellationToken.None);

        var names = result.Symbols.Select(s => s.Name).ToList();
        names.ShouldContain("Account");
        names.ShouldContain("Deposit");
        names.ShouldContain("Withdraw");
        names.ShouldContain("Id");
    }

    [Fact]
    public async Task SearchSymbols_Kinds_Filter_Restricts_Results()
    {
        var service = CreateService("Demo", ("Models.cs",
            "public class Account { public int Id { get; set; } public void Deposit() { } }"));

        var result = await service.SearchSymbolsAsync("Demo", null, "Models.cs", ["method"], 50, CancellationToken.None);

        result.Symbols.ShouldAllBe(s => s.Kind == "method");
        result.Symbols.Select(s => s.Name).ShouldContain("Deposit");
    }

    [Fact]
    public async Task GetSymbolInfo_Returns_Kind_BaseTypes_And_Interfaces()
    {
        var service = CreateService("Demo", ("Pets.cs",
            "public interface IPet { } public class Animal { } public class Dog : Animal, IPet { }"));

        var result = await service.GetSymbolInfoAsync("Demo", "Dog", includeSource: true, CancellationToken.None);

        result.Kind.ShouldBe("class");
        result.BaseTypes.ShouldContain("Animal");
        result.Interfaces.ShouldContain("IPet");
        result.Source.ShouldNotBeNull();
        result.Source!.ShouldContain("class Dog");
    }

    [Fact]
    public async Task GetSymbolInfo_Unknown_Symbol_Throws_KeyNotFound()
    {
        var service = CreateService("Demo", ("A.cs", "public class A { }"));

        await Should.ThrowAsync<KeyNotFoundException>(
            () => service.GetSymbolInfoAsync("Demo", "DoesNotExist", false, CancellationToken.None));
    }

    [Fact]
    public async Task FindReferences_Finds_Use_Sites()
    {
        var service = CreateService("Demo", ("Calc.cs",
            "public class Calc { public int Add(int a, int b) { return a + b; } public int Twice(int x) { return Add(x, x); } }"));

        var result = await service.FindReferencesAsync("Demo", "Add", includeDefinition: false, 100, CancellationToken.None);

        result.TotalReferences.ShouldBeGreaterThanOrEqualTo(1);
        result.References.ShouldContain(r => r.Snippet.Contains("Add(x, x)"));
    }

    [Fact]
    public async Task FindImplementations_Interface_Returns_Implementors()
    {
        var service = CreateService("Demo", ("Repo.cs",
            "public interface IRepository { } public class SqlRepository : IRepository { } public class InMemoryRepository : IRepository { }"));

        var result = await service.FindImplementationsAsync("Demo", "IRepository", 100, CancellationToken.None);

        result.Implementations.Select(s => s.Name).ShouldBe(["InMemoryRepository", "SqlRepository"], ignoreOrder: true);
    }

    [Fact]
    public async Task GetTypeHierarchy_Base_Returns_Base_Chain()
    {
        var service = CreateService("Demo", ("Shapes.cs",
            "public class Shape { } public class Polygon : Shape { } public class Square : Polygon { }"));

        var result = await service.GetTypeHierarchyAsync("Demo", "Square", "base", 100, CancellationToken.None);

        result.BaseTypes.ShouldNotBeNull();
        result.BaseTypes!.Select(s => s.Name).ShouldBe(["Polygon", "Shape"]);
    }

    [Fact]
    public async Task GetTypeHierarchy_Derived_Returns_Subclasses()
    {
        var service = CreateService("Demo", ("Shapes.cs",
            "public class Shape { } public class Polygon : Shape { } public class Circle : Shape { }"));

        var result = await service.GetTypeHierarchyAsync("Demo", "Shape", "derived", 100, CancellationToken.None);

        result.DerivedTypes.ShouldNotBeNull();
        result.DerivedTypes!.Select(s => s.Name).ShouldBe(["Circle", "Polygon"], ignoreOrder: true);
    }

    [Fact]
    public async Task GetCallGraph_Callers_Finds_Calling_Method()
    {
        var service = CreateService("Demo", ("Flow.cs",
            "public class Flow { public void Leaf() { } public void Middle() { Leaf(); } }"));

        var result = await service.GetCallGraphAsync("Demo", "Leaf", "callers", 1, 50, CancellationToken.None);

        result.Callers.ShouldNotBeNull();
        result.Callers!.ShouldContain(n => n.FullName.Contains("Flow.Middle"));
    }

    [Fact]
    public async Task GetCallGraph_Callees_Finds_Called_Method()
    {
        var service = CreateService("Demo", ("Flow.cs",
            "public class Flow { public void Leaf() { } public void Middle() { Leaf(); } }"));

        var result = await service.GetCallGraphAsync("Demo", "Middle", "callees", 1, 50, CancellationToken.None);

        result.Callees.ShouldNotBeNull();
        result.Callees!.ShouldContain(n => n.FullName.Contains("Flow.Leaf"));
    }

    [Fact]
    public async Task GetCallGraph_Rejects_NonMethod_Symbol()
    {
        var service = CreateService("Demo", ("A.cs", "public class A { }"));

        await Should.ThrowAsync<ArgumentException>(
            () => service.GetCallGraphAsync("Demo", "A", "callers", 1, 50, CancellationToken.None));
    }

    [Fact]
    public async Task GetSymbolInfo_Overload_Resolves_With_Parameter_Qualified_Name()
    {
        var service = CreateService("Demo", ("Calc.cs",
            "public class Calc { public int Add(int a, int b) => a + b; public double Add(double a, double b) => a + b; }"));

        var result = await service.GetSymbolInfoAsync("Demo", "Calc.Add(int, int)", includeSource: false, CancellationToken.None);

        result.Name.ShouldBe("Add");
        result.Signature.ShouldContain("int a, int b");
    }

    [Fact]
    public async Task GetSymbolInfo_Ambiguous_Overload_Lists_Distinguishable_Candidates()
    {
        var service = CreateService("Demo", ("Calc.cs",
            "public class Calc { public int Add(int a, int b) => a + b; public double Add(double a, double b) => a + b; }"));

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => service.GetSymbolInfoAsync("Demo", "Add", false, CancellationToken.None));

        ex.Message.ShouldContain("Add(int, int)");
        ex.Message.ShouldContain("Add(double, double)");
    }

    [Fact]
    public async Task GetCallGraph_Does_Not_False_Cycle_Across_Overloads()
    {
        var service = CreateService("Demo", ("Flow.cs",
            "public class C { public void Root() { Foo(1); } public void Foo(int x) { Foo(\"s\"); } public void Foo(string s) { Bar(); } public void Bar() { } }"));

        var result = await service.GetCallGraphAsync("Demo", "Root", "callees", 3, 50, CancellationToken.None);

        // Root -> Foo(int) -> Foo(string) -> Bar. The two Foo overloads must not collide as a false
        // cycle, so Bar must remain reachable in the returned graph.
        static bool Reaches(IEnumerable<RoselineMCP.Models.CallGraphNode>? nodes, string name) =>
            nodes != null && nodes.Any(n => n.FullName.Contains(name) || Reaches(n.Children, name));

        Reaches(result.Callees, "C.Bar").ShouldBeTrue();
    }

    [Fact]
    public async Task SearchSymbols_File_Suffix_Does_Not_Match_Longer_Filename()
    {
        var service = CreateService("Demo", ("UserService.cs", "public class UserService { }"));

        // "Service.cs" must not resolve to "UserService.cs" — it should report the file as missing.
        await Should.ThrowAsync<KeyNotFoundException>(
            () => service.SearchSymbolsAsync("Demo", null, "Service.cs", null, 50, CancellationToken.None));
    }

    [Fact]
    public async Task SearchSymbols_File_Outline_Is_Lean()
    {
        var service = CreateService("Demo", ("Models.cs",
            "public class Account { public int Id { get; set; } public void Deposit() { } }"));

        var result = await service.SearchSymbolsAsync("Demo", null, "Models.cs", null, 50, CancellationToken.None);

        // The outline omits per-symbol file, fully-qualified name, and accessibility — the file is on
        // the response and accessibility is inside the signature — but keeps name/kind/signature.
        result.Symbols.ShouldAllBe(s => s.File == null && s.FullName == null && s.Accessibility == null);
        result.Symbols.ShouldContain(s => s.Name == "Deposit" && s.Signature.Length > 0);
    }

    [Fact]
    public async Task SearchSymbols_ProjectWide_Keeps_FullName_And_File()
    {
        var service = CreateService("Demo", ("Services.cs", "public class UserService { }"));

        var result = await service.SearchSymbolsAsync("Demo", "UserService", null, null, 50, CancellationToken.None);

        var summary = result.Symbols.ShouldHaveSingleItem();
        summary.FullName.ShouldNotBeNull();
        summary.File.ShouldNotBeNull();
    }

    // --- Solution-wide symbol search/resolution: symbols in sibling projects the anchor does NOT
    // reference (e.g. the Tests project when the anchor is the main project) must still be found.

    private static CodeNavigationService CreateSolutionService(
        (string ProjectName, (string Name, string Code)[] Files)[] projects,
        (string From, string To)[]? projectReferences = null)
    {
        var (workspace, anchor) = AdhocProjectBuilder.CreateSolution(projects, projectReferences);
        var loader = AdhocProjectBuilder.FakeLoaderFor(workspace, anchor);
        return new CodeNavigationService(A.Fake<ILogger<CodeNavigationService>>(), loader);
    }

    [Fact]
    public async Task SearchSymbols_Finds_Types_In_Unreferenced_Sibling_Project()
    {
        var service = CreateSolutionService(
        [
            ("App", [("App.cs", "namespace AppNs { public class AppRoot { } }")]),
            ("App.Tests", [("WidgetTests.cs", "namespace TestNs { public class WidgetTests { public void Runs() { } } }")])
        ]);

        var result = await service.SearchSymbolsAsync("App", "*Tests", null, null, 50, CancellationToken.None);

        result.Symbols.ShouldContain(s => s.Name == "WidgetTests");
    }

    [Fact]
    public async Task GetSymbolInfo_Resolves_Symbol_Declared_Only_In_Unreferenced_Sibling_Project()
    {
        var service = CreateSolutionService(
        [
            ("App", [("App.cs", "namespace AppNs { public class AppRoot { } }")]),
            ("Lib", [("Widget.cs", "namespace LibNs { public class Widget { public void Spin() { } } }")])
        ]);

        var result = await service.GetSymbolInfoAsync("App", "Widget", includeSource: false, CancellationToken.None);

        result.Kind.ShouldBe("class");
        result.FullName.ShouldBe("LibNs.Widget");
    }

    [Fact]
    public async Task GetSymbolInfo_FullyQualified_Name_Resolves_Across_Unreferenced_Projects()
    {
        var service = CreateSolutionService(
        [
            ("App", [("App.cs", "namespace AppNs { public class AppRoot { } }")]),
            ("Lib", [("Widget.cs", "namespace LibNs { public class Widget { } }")])
        ]);

        // Exercises the GetTypeByMetadataName fast path: only Lib's compilation knows this type.
        var result = await service.GetSymbolInfoAsync("App", "LibNs.Widget", includeSource: false, CancellationToken.None);

        result.FullName.ShouldBe("LibNs.Widget");
    }

    [Fact]
    public async Task GetSymbolInfo_Same_Name_In_Two_Projects_Is_Ambiguous_Listing_Both()
    {
        var service = CreateSolutionService(
        [
            ("App", [("A.cs", "namespace AppNs { public class Duplicate { } }")]),
            ("Lib", [("B.cs", "namespace LibNs { public class Duplicate { } }")])
        ]);

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => service.GetSymbolInfoAsync("App", "Duplicate", false, CancellationToken.None));

        ex.Message.ShouldContain("AppNs.Duplicate");
        ex.Message.ShouldContain("LibNs.Duplicate");
    }

    [Fact]
    public async Task GetSymbolInfo_Referenced_Project_Declaration_Is_Not_A_False_Ambiguity()
    {
        // App references Lib, so Lib's Widget is visible from BOTH compilations. The same
        // declaration must be deduplicated (SymbolEqualityComparer does not equate the instances),
        // not reported as ambiguous.
        var service = CreateSolutionService(
        [
            ("App", [("App.cs", "namespace AppNs { public class AppRoot { } }")]),
            ("Lib", [("Widget.cs", "namespace LibNs { public class Widget { } }")])
        ],
        projectReferences: [("App", "Lib")]);

        var bySimpleName = await service.GetSymbolInfoAsync("App", "Widget", false, CancellationToken.None);
        bySimpleName.FullName.ShouldBe("LibNs.Widget");

        var byFullName = await service.GetSymbolInfoAsync("App", "LibNs.Widget", false, CancellationToken.None);
        byFullName.FullName.ShouldBe("LibNs.Widget");
    }

    [Fact]
    public async Task FindReferences_Resolves_Symbol_In_Unreferenced_Sibling_Project()
    {
        var service = CreateSolutionService(
        [
            ("App", [("App.cs", "namespace AppNs { public class AppRoot { } }")]),
            ("Lib", [("Widget.cs", "namespace LibNs { public class Widget { public void Spin() { } public void Use() { Spin(); } } }")])
        ]);

        var result = await service.FindReferencesAsync("App", "Spin", includeDefinition: false, 100, CancellationToken.None);

        result.References.ShouldContain(r => r.Snippet.Contains("Spin()"));
    }

    [Fact]
    public async Task SearchSymbols_File_Outline_Finds_File_In_Unreferenced_Sibling_Project()
    {
        var service = CreateSolutionService(
        [
            ("App", [("App.cs", "namespace AppNs { public class AppRoot { } }")]),
            ("Lib", [("Widget.cs", "namespace LibNs { public class Widget { public void Spin() { } } }")])
        ]);

        var result = await service.SearchSymbolsAsync("App", null, "Widget.cs", null, 50, CancellationToken.None);

        result.Symbols.Select(s => s.Name).ShouldContain("Spin");
    }

    // --- get_symbol_at_position: file:line(:column) → symbol ---

    /// <summary>
    /// Line 3 declares Deposit ('D' at column 17); line 5 calls Log ('L' at column 9); line 8
    /// declares Log.
    /// </summary>
    private const string AccountSource =
        "public class Account\n" +
        "{\n" +
        "    public void Deposit(int amount)\n" +
        "    {\n" +
        "        Log(amount);\n" +
        "    }\n" +
        "\n" +
        "    public void Log(int value)\n" +
        "    {\n" +
        "    }\n" +
        "}\n";

    [Fact]
    public async Task GetSymbolAtPosition_On_Method_Declaration_Returns_That_Method_As_Declaration()
    {
        var service = CreateService("Demo", ("Account.cs", AccountSource));

        var result = await service.GetSymbolAtPositionAsync("Demo", "Account.cs", 3, 17, CancellationToken.None);

        result.Name.ShouldBe("Deposit");
        result.Kind.ShouldBe("method");
        result.IsDeclaration.ShouldBeTrue();
        result.ContainingType.ShouldBe("Account");
        result.DefinitionFile.ShouldBe("Account.cs");
        result.DefinitionLine.ShouldBe(3);
    }

    [Fact]
    public async Task GetSymbolAtPosition_On_Usage_Returns_The_Referenced_Symbol()
    {
        var service = CreateService("Demo", ("Account.cs", AccountSource));

        var result = await service.GetSymbolAtPositionAsync("Demo", "Account.cs", 5, 9, CancellationToken.None);

        result.Name.ShouldBe("Log");
        result.Kind.ShouldBe("method");
        result.IsDeclaration.ShouldBeFalse();
        result.DefinitionLine.ShouldBe(8);
    }

    [Fact]
    public async Task GetSymbolAtPosition_LineOnly_On_Declaration_Line_Prefers_The_Declaration()
    {
        var service = CreateService("Demo", ("Account.cs", AccountSource));

        var result = await service.GetSymbolAtPositionAsync("Demo", "Account.cs", 3, null, CancellationToken.None);

        result.Name.ShouldBe("Deposit");
        result.IsDeclaration.ShouldBeTrue();
    }

    [Fact]
    public async Task GetSymbolAtPosition_LineOnly_On_Usage_Line_Returns_The_Referenced_Symbol()
    {
        var service = CreateService("Demo", ("Account.cs", AccountSource));

        var result = await service.GetSymbolAtPositionAsync("Demo", "Account.cs", 5, null, CancellationToken.None);

        result.Name.ShouldBe("Log");
        result.IsDeclaration.ShouldBeFalse();
    }

    [Fact]
    public async Task GetSymbolAtPosition_On_Modifier_Column_Falls_Back_To_The_Enclosing_Declaration()
    {
        var service = CreateService("Demo", ("Account.cs", AccountSource));

        // Column 5 is the 'p' of 'public' on Deposit's declaration line — no token binds there,
        // so the enclosing declaration wins.
        var result = await service.GetSymbolAtPositionAsync("Demo", "Account.cs", 3, 5, CancellationToken.None);

        result.Name.ShouldBe("Deposit");
        result.IsDeclaration.ShouldBeTrue();
    }

    [Fact]
    public async Task GetSymbolAtPosition_Out_Of_Range_Line_Throws_ArgumentException()
    {
        var service = CreateService("Demo", ("Account.cs", AccountSource));

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => service.GetSymbolAtPositionAsync("Demo", "Account.cs", 999, null, CancellationToken.None));

        ex.Message.ShouldContain("999");
        ex.Message.ShouldContain("lines");
    }

    [Fact]
    public async Task GetSymbolAtPosition_Out_Of_Range_Column_Throws_ArgumentException()
    {
        var service = CreateService("Demo", ("Account.cs", AccountSource));

        await Should.ThrowAsync<ArgumentException>(
            () => service.GetSymbolAtPositionAsync("Demo", "Account.cs", 3, 999, CancellationToken.None));
    }

    [Fact]
    public async Task GetSymbolAtPosition_Unknown_File_Throws_KeyNotFound()
    {
        var service = CreateService("Demo", ("Account.cs", AccountSource));

        await Should.ThrowAsync<KeyNotFoundException>(
            () => service.GetSymbolAtPositionAsync("Demo", "Missing.cs", 1, null, CancellationToken.None));
    }

    [Fact]
    public async Task GetSymbolAtPosition_Resolves_File_In_Unreferenced_Sibling_Project()
    {
        var service = CreateSolutionService(
        [
            ("App", [("App.cs", "namespace AppNs { public class AppRoot { } }")]),
            ("Lib", [("Widget.cs",
                "namespace LibNs\n" +
                "{\n" +
                "    public class Widget\n" +
                "    {\n" +
                "        public void Spin()\n" +
                "        {\n" +
                "        }\n" +
                "    }\n" +
                "}\n")])
        ]);

        var result = await service.GetSymbolAtPositionAsync("App", "Widget.cs", 5, null, CancellationToken.None);

        result.Name.ShouldBe("Spin");
        result.FullName.ShouldBe("LibNs.Widget.Spin");
        result.IsDeclaration.ShouldBeTrue();
    }

    /// <summary>
    /// Builds a service over a project at a known base directory, so the resolved-path assertions
    /// below have something concrete to compare against (the default helper uses a random temp dir).
    /// </summary>
    private static (CodeNavigationService Service, string Csproj) CreateServiceAt(string baseDirectory)
    {
        var (workspace, project) = AdhocProjectBuilder.Create(
            "Acme",
            [("A.cs", "public interface IWidget { void Spin(); } public class Widget : IWidget { public void Spin() { Turn(); } public void Turn() { } }")],
            baseDirectory);
        var loader = AdhocProjectBuilder.FakeLoaderFor(workspace, project);
        return (new CodeNavigationService(A.Fake<ILogger<CodeNavigationService>>(), loader),
                Path.Combine(baseDirectory, "Acme.csproj"));
    }

    private static string FreshBaseDir() =>
        Path.Combine(Path.GetTempPath(), "roseline-tests", Guid.NewGuid().ToString("n"));

    /// <summary>
    /// Regression guard for the silent wrong-checkout bug: two checkouts of the same repository
    /// report the same project name and the same relative paths, so the absolute resolved path is
    /// the only field that tells them apart. It must be present on every navigation payload.
    /// </summary>
    [Fact]
    public async Task SearchSymbols_ReportsTheResolvedProjectPath()
    {
        var (service, csproj) = CreateServiceAt(FreshBaseDir());

        var response = await service.SearchSymbolsAsync(null, "Widget", null, null, 50, CancellationToken.None);

        response.ResolvedPath.ShouldBe(csproj);
    }

    [Fact]
    public async Task GetSymbolInfo_ReportsTheResolvedProjectPath()
    {
        var (service, csproj) = CreateServiceAt(FreshBaseDir());

        var response = await service.GetSymbolInfoAsync(null, "Widget", includeSource: false, CancellationToken.None);

        response.ResolvedPath.ShouldBe(csproj);
    }

    [Fact]
    public async Task GetSymbolAtPosition_ReportsTheResolvedProjectPath()
    {
        var (service, csproj) = CreateServiceAt(FreshBaseDir());

        var response = await service.GetSymbolAtPositionAsync(null, "A.cs", 1, null, CancellationToken.None);

        response.ResolvedPath.ShouldBe(csproj);
    }

    [Fact]
    public async Task FindReferences_ReportsTheResolvedProjectPath()
    {
        var (service, csproj) = CreateServiceAt(FreshBaseDir());

        var response = await service.FindReferencesAsync(null, "Widget.Turn", includeDefinition: true, 50, CancellationToken.None);

        response.ResolvedPath.ShouldBe(csproj);
    }

    [Fact]
    public async Task FindImplementations_ReportsTheResolvedProjectPath()
    {
        var (service, csproj) = CreateServiceAt(FreshBaseDir());

        var response = await service.FindImplementationsAsync(null, "IWidget", 50, CancellationToken.None);

        response.ResolvedPath.ShouldBe(csproj);
    }

    [Fact]
    public async Task GetCallGraph_ReportsTheResolvedProjectPath()
    {
        var (service, csproj) = CreateServiceAt(FreshBaseDir());

        var response = await service.GetCallGraphAsync(null, "Widget.Spin", "both", 1, 50, CancellationToken.None);

        response.ResolvedPath.ShouldBe(csproj);
    }

    [Fact]
    public async Task GetTypeHierarchy_ReportsTheResolvedProjectPath()
    {
        var (service, csproj) = CreateServiceAt(FreshBaseDir());

        var response = await service.GetTypeHierarchyAsync(null, "Widget", "both", 50, CancellationToken.None);

        response.ResolvedPath.ShouldBe(csproj);
    }
}
