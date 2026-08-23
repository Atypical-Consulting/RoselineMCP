using System.Reflection;
using Microsoft.CodeAnalysis;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// The smallest possible <see cref="IAnalyzerAssemblyLoader"/>: what Roslyn asks of a host in order
/// to turn an <c>AnalyzerFileReference</c> into analyzer instances. Loads into the default context,
/// which is what lets a test-built analyzer resolve the <c>Microsoft.CodeAnalysis</c> already
/// in-process.
/// </summary>
internal sealed class TestAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
{
    public static TestAnalyzerAssemblyLoader Instance { get; } = new();

    public void AddDependencyLocation(string fullPath) { }

    public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
}
