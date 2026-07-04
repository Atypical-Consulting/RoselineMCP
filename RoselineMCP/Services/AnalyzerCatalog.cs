using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;

namespace RoselineMCP.Services;

/// <summary>
/// Discovers and loads the analyzer/code-fix assemblies bundled with RoselineMCP.
///
/// The Roslynator NuGet packages are analyzer-asset-only (no <c>lib/</c> folder), so their
/// assemblies never land in the build output through a normal package reference —
/// <c>Assembly.Load("Roslynator.CodeFixes")</c> can never succeed. Instead, RoselineMCP.csproj
/// mirrors the packages' <c>analyzers/dotnet/roslyn4.7/cs/*.dll</c> into an <c>analyzers/</c>
/// folder next to RoselineMCP.dll, and this catalog loads every DLL found there via
/// <see cref="Assembly.LoadFrom(string)"/> (whose same-directory probing also resolves the
/// packages' prefixed internal dependencies, e.g. <c>Roslynator_CodeFixes_Roslynator.Common</c>).
///
/// Loading is lazy, happens once, and is deliberately forgiving: a DLL that fails to load or a
/// type that fails to instantiate is logged and skipped — a broken bundled assembly must degrade
/// analyzer coverage, never break the diagnostics tools.
/// </summary>
public class AnalyzerCatalog : IAnalyzerCatalog
{
    /// <summary>Name of the bundled-analyzers folder, relative to RoselineMCP.dll.</summary>
    internal const string AnalyzersDirectoryName = "analyzers";

    private readonly ILogger<AnalyzerCatalog> _logger;
    private readonly Lazy<(ImmutableArray<DiagnosticAnalyzer> Analyzers, IReadOnlyList<Assembly> Assemblies)> _loaded;

    /// <summary>
    /// Initializes a new instance of the AnalyzerCatalog.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public AnalyzerCatalog(ILogger<AnalyzerCatalog> logger)
    {
        _logger = logger;
        _loaded = new Lazy<(ImmutableArray<DiagnosticAnalyzer>, IReadOnlyList<Assembly>)>(
            Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc/>
    public ImmutableArray<DiagnosticAnalyzer> Analyzers => _loaded.Value.Analyzers;

    /// <inheritdoc/>
    public IReadOnlyList<Assembly> Assemblies => _loaded.Value.Assemblies;

    private (ImmutableArray<DiagnosticAnalyzer>, IReadOnlyList<Assembly>) Load()
    {
        var directory = ResolveAnalyzersDirectory();
        if (directory == null)
        {
            _logger.LogWarning(
                "Bundled analyzers directory not found next to RoselineMCP.dll — analyzer-driven " +
                "diagnostics (RCS*) and Roslynator code fixes will be unavailable");
            return (ImmutableArray<DiagnosticAnalyzer>.Empty, Array.Empty<Assembly>());
        }

        var assemblies = LoadAssemblies(directory);
        var analyzers = InstantiateAnalyzers(assemblies);

        _logger.LogInformation(
            "Loaded {AssemblyCount} bundled analyzer assemblies with {AnalyzerCount} C# analyzers from {Directory}",
            assemblies.Count, analyzers.Length, directory);

        return (analyzers, assemblies);
    }

    /// <summary>
    /// Resolves the bundled <c>analyzers/</c> directory relative to where RoselineMCP.dll
    /// actually lives (works from the app's own output, the packed dotnet tool, and a test
    /// project's output, where the folder flows in via the project reference), falling back to
    /// <see cref="AppContext.BaseDirectory"/> for hosts where <see cref="Assembly.Location"/>
    /// is empty (e.g. single-file publish).
    /// </summary>
    private static string? ResolveAnalyzersDirectory()
    {
        var assemblyLocation = typeof(AnalyzerCatalog).Assembly.Location;
        string?[] candidates =
        [
            string.IsNullOrEmpty(assemblyLocation) ? null : Path.GetDirectoryName(assemblyLocation),
            AppContext.BaseDirectory
        ];

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            var directory = Path.Combine(candidate, AnalyzersDirectoryName);
            if (Directory.Exists(directory))
            {
                return directory;
            }
        }

        return null;
    }

    private List<Assembly> LoadAssemblies(string directory)
    {
        var assemblies = new List<Assembly>();

        // Ordinal sort for deterministic load/registration order across machines.
        foreach (var dll in Directory.GetFiles(directory, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                assemblies.Add(Assembly.LoadFrom(dll));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not load bundled analyzer assembly {File}: {Message}",
                    Path.GetFileName(dll), ex.Message);
            }
        }

        return assemblies;
    }

    private ImmutableArray<DiagnosticAnalyzer> InstantiateAnalyzers(IReadOnlyList<Assembly> assemblies)
    {
        var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Keep whatever types did resolve; the rest are logged and skipped.
                types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
                _logger.LogWarning("Some types failed to load from {Assembly}: {Message}",
                    assembly.GetName().Name, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not enumerate types in {Assembly}: {Message}",
                    assembly.GetName().Name, ex.Message);
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || !typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
                {
                    continue;
                }

                var attribute = type.GetCustomAttribute<DiagnosticAnalyzerAttribute>();
                if (attribute == null || !attribute.Languages.Contains(LanguageNames.CSharp))
                {
                    continue;
                }

                if (!seenTypes.Add(type.FullName!))
                {
                    continue;
                }

                try
                {
                    builder.Add((DiagnosticAnalyzer)Activator.CreateInstance(type)!);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Could not instantiate analyzer {Type}: {Message}", type.FullName, ex.Message);
                }
            }
        }

        return builder.ToImmutable();
    }
}
