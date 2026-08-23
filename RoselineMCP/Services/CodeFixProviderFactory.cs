using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;

namespace RoselineMCP.Services;

/// <summary>
/// Factory for creating and managing code fix providers.
/// </summary>
/// <remarks>
/// <para>
/// Two layers (see <see cref="ICodeFixProviderFactory"/>). The process-wide map is built once, in
/// the constructor, from the Roslyn built-ins and the bundled analyzer catalog — the factory is a
/// singleton with no per-project state, which is why the original design could not reach a target
/// project's own fixers. The project overlay closes that gap without touching the map: for each
/// <see cref="AnalyzerFileReference"/> of a project, the <see cref="CodeFixProvider"/> types of its
/// assembly are reflected once and cached per reference object (a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/>, so the cache lives exactly as long as the
/// workspace that holds the reference).
/// </para>
/// <para>
/// The overlay loads each assembly through the reference's <b>own</b>
/// <see cref="IAnalyzerAssemblyLoader"/> — the loader the diagnostics pass already used to run that
/// reference's analyzers — so it adds no assembly to the process that the analyzer pass does not
/// already load and execute; it instantiates additional <em>types</em> from assemblies that are
/// already resident. <c>SECURITY.md</c> records this as a decision, not an accident.
/// </para>
/// <para>
/// Lookup order is process-wide map first, overlay second: an ID both can fix resolves to the
/// bundled provider, so behaviour cannot regress for an already-fixable ID.
/// </para>
/// </remarks>
public class CodeFixProviderFactory : ICodeFixProviderFactory
{
    private static readonly FrozenDictionary<string, Type> NoProviders =
        FrozenDictionary<string, Type>.Empty;

    private readonly ILogger<CodeFixProviderFactory> _logger;
    private readonly IAnalyzerCatalog? _analyzerCatalog;
    private readonly Dictionary<string, Type> _providers = new();
    private readonly ConditionalWeakTable<AnalyzerReference, FrozenDictionary<string, Type>> _overlays = new();
    private bool _providersLoaded;

    /// <summary>
    /// Initializes a new instance of the CodeFixProviderFactory.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="analyzerCatalog">
    /// Catalog of the bundled analyzer/fixer assemblies (Roslynator), which are scanned for
    /// code fix providers in addition to the Roslyn built-ins. Optional so the factory can be
    /// constructed without it (built-in fixers only); production DI always supplies it.
    /// </param>
    public CodeFixProviderFactory(ILogger<CodeFixProviderFactory> logger, IAnalyzerCatalog? analyzerCatalog = null)
    {
        _logger = logger;
        _analyzerCatalog = analyzerCatalog;
        LoadProviders();
    }

    /// <inheritdoc/>
    public CodeFixProvider? GetProviderForDiagnostic(string diagnosticId) =>
        GetProviderForDiagnostic(diagnosticId, project: null);

    /// <inheritdoc/>
    public CodeFixProvider? GetProviderForDiagnostic(string diagnosticId, Project? project)
    {
        ArgumentNullException.ThrowIfNull(diagnosticId);

        if (_providers.TryGetValue(diagnosticId, out var providerType))
        {
            return Instantiate(providerType, diagnosticId);
        }

        if (project is null)
        {
            return null;
        }

        foreach (var reference in project.AnalyzerReferences)
        {
            if (OverlayFor(reference).TryGetValue(diagnosticId, out providerType))
            {
                return Instantiate(providerType, diagnosticId);
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public IEnumerable<string> GetFixableDiagnosticIds() => GetFixableDiagnosticIds(project: null);

    /// <inheritdoc/>
    public IEnumerable<string> GetFixableDiagnosticIds(Project? project)
    {
        if (project is null)
        {
            return _providers.Keys;
        }

        var ids = new HashSet<string>(_providers.Keys, StringComparer.Ordinal);
        foreach (var reference in project.AnalyzerReferences)
        {
            ids.UnionWith(OverlayFor(reference).Keys);
        }

        return ids;
    }

    /// <inheritdoc/>
    public void LoadProviders()
    {
        if (_providersLoaded)
        {
            return;
        }

        try
        {
            var assemblies = GetAssembliesToScan();

            foreach (var assembly in assemblies)
            {
                LoadProvidersFromAssembly(assembly);
            }

            _providersLoaded = true;
            _logger.LogInformation("Loaded {Count} code fix providers", _providers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load code fix providers");
        }
    }

    private CodeFixProvider? Instantiate(Type providerType, string diagnosticId)
    {
        try
        {
            return Activator.CreateInstance(providerType) as CodeFixProvider;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create code fix provider for {DiagnosticId}", diagnosticId);
            return null;
        }
    }

    private List<Assembly> GetAssembliesToScan()
    {
        // Built-in Roslyn fixers first: registration is first-wins per diagnostic ID, so the
        // built-in provider keeps precedence for IDs both it and Roslynator can fix.
        var assemblies = new List<Assembly> { typeof(CodeFixProvider).Assembly };

        TryLoadAssembly(assemblies, "Microsoft.CodeAnalysis.Features");
        TryLoadAssembly(assemblies, "Microsoft.CodeAnalysis.CSharp.Features");
        // Kept for completeness, but Roslynator ships as analyzer-asset-only packages (no lib/),
        // so this name-based load never succeeds from the build output — the Roslynator fixers
        // actually come from the bundled analyzer catalog below.
        TryLoadAssembly(assemblies, "Roslynator.CodeFixes");

        if (_analyzerCatalog != null)
        {
            assemblies.AddRange(_analyzerCatalog.Assemblies);
        }

        return assemblies.Where(a => a != null).ToList();
    }

    private void TryLoadAssembly(List<Assembly> assemblies, string assemblyName)
    {
        try
        {
            assemblies.Add(Assembly.Load(assemblyName));
        }
        catch
        {
            _logger.LogDebug("Could not load assembly {AssemblyName}", assemblyName);
        }
    }

    private void LoadProvidersFromAssembly(Assembly assembly)
    {
        try
        {
            foreach (var type in ProviderTypes(assembly))
            {
                RegisterProvider(_providers, type);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error loading code fix providers from assembly {Assembly}: {Message}",
                assembly.FullName, ex.Message);
        }
    }

    /// <summary>
    /// The concrete <see cref="CodeFixProvider"/> types of <paramref name="assembly"/>. An assembly
    /// some of whose types cannot be loaded (a dependency bound to a newer Roslyn, say) still yields
    /// the types that can: the same "degrade, never fail" rule the analyzer pass follows.
    /// </summary>
    private static IEnumerable<Type> ProviderTypes(Assembly assembly)
    {
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        return types.Where(t => t is { IsAbstract: false } && t.IsSubclassOf(typeof(CodeFixProvider)))!;
    }

    private void RegisterProvider(IDictionary<string, Type> map, Type type)
    {
        try
        {
            if (Activator.CreateInstance(type) is CodeFixProvider instance)
            {
                foreach (var id in instance.FixableDiagnosticIds)
                {
                    if (!map.ContainsKey(id))
                    {
                        map[id] = type;
                        _logger.LogDebug("Registered code fix provider for {DiagnosticId}: {Provider}",
                            id, type.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not instantiate code fix provider {Type}: {Message}",
                type.Name, ex.Message);
        }
    }

    /// <summary>
    /// The providers carried by one analyzer reference, reflected once per reference object.
    /// </summary>
    private FrozenDictionary<string, Type> OverlayFor(AnalyzerReference reference) =>
        _overlays.GetValue(reference, BuildOverlay);

    private FrozenDictionary<string, Type> BuildOverlay(AnalyzerReference reference)
    {
        // Only a file reference has an assembly to reflect over, and only through its own loader:
        // an in-memory reference (AnalyzerImageReference) carries analyzer instances, not fixers.
        if (reference is not AnalyzerFileReference { FullPath: { } path } fileReference)
        {
            return NoProviders;
        }

        try
        {
            var assembly = fileReference.AssemblyLoader.LoadFromPath(path);
            var map = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var type in ProviderTypes(assembly))
            {
                RegisterProvider(map, type);
            }

            if (map.Count > 0)
            {
                _logger.LogDebug("Registered {Count} fixable ID(s) from project reference {Reference}",
                    map.Count, reference.Display);
            }

            return map.ToFrozenDictionary(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // The same rule as the analyzer pass: one unreadable reference never fails the lookup.
            _logger.LogDebug("Could not load code fix providers from project reference {Reference}: {Message}",
                reference.Display, ex.Message);
            return NoProviders;
        }
    }
}
