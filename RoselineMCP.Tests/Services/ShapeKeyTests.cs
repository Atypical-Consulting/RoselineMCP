using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class ShapeKeyTests
{
    private static InvocationExpressionSyntax FirstCall(string source)
        => CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().First();

    [Fact]
    public void Same_Call_In_Same_Position_Yields_Equal_Keys_And_Parent_Kind_Changes_It()
    {
        var a = FirstCall("class C { void M() { Foo(); } }");
        var b = FirstCall("class C { void N() { Foo(); } }");
        var c = FirstCall("class C { void M() { var x = Foo(); } }");

        ShapeKey.From(a, null).Key.ShouldBe(ShapeKey.From(b, null).Key);
        ShapeKey.From(a, null).Key.ShouldNotBe(ShapeKey.From(c, null).Key);
    }

    [Fact]
    public void Resolved_Symbol_Is_The_Fourth_Component_And_Null_Model_Degrades_To_Kinds()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { static void Foo() {} void M() { Foo(); } }");
        var compilation = CSharpCompilation.Create("t", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var call = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().First();

        var withModel = ShapeKey.From(call, compilation.GetSemanticModel(tree));
        var without = ShapeKey.From(call, null);

        withModel.MatchedOn.ShouldBe("kinds+symbol");
        withModel.Key.ShouldStartWith(without.Key[..^1]);
        withModel.Key.ShouldContain("src|");
        without.MatchedOn.ShouldBe("kinds");
        without.Key.ShouldEndWith("|-");
    }
}
