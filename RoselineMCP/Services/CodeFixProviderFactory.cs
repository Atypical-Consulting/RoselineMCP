using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using System.Reflection;

namespace RoselineMCP.Services;

/// <summary>
/// Factory for creating and managing code fix providers.
/// </summary>
public class CodeFixProviderFactory : ICodeFixProviderFactory
{
    private readonly ILogger<CodeFixProviderFactory> _logger;
    private readonly IAnalyzerCatalog? _analyzerCatalog;
    private readonly Dictionary<string, Type> _providers = new();
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
    public CodeFixProvider? GetProviderForDiagnostic(string diagnosticId)
    {
        if (!_providers.TryGetValue(diagnosticId, out var providerType))
        {
            return null;
        }

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

    /// <inheritdoc/>
    public IEnumerable<string> GetFixableDiagnosticIds()
    {
        return _providers.Keys;
    }

    /// <inheritdoc/>
    public void LoadProviders()
    {
        if (_providersLoaded) return;

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
            var types = assembly.GetTypes()
                .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(CodeFixProvider)));

            foreach (var type in types)
            {
                RegisterProvider(type);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error loading code fix providers from assembly {Assembly}: {Message}",
                assembly.FullName, ex.Message);
        }
    }

    private void RegisterProvider(Type type)
    {
        try
        {
            var instance = Activator.CreateInstance(type) as CodeFixProvider;
            if (instance != null)
            {
                foreach (var id in instance.FixableDiagnosticIds)
                {
                    if (!_providers.ContainsKey(id))
                    {
                        _providers[id] = type;
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
}