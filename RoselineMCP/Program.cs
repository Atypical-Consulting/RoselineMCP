using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Diagnostics;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
try
{
    // Build and run the host
    var host = CreateHostBuilder(args).Build();

    // Log startup information
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("RoselineMCP server starting...");
    logger.LogInformation("Process ID: {ProcessId}", Environment.ProcessId);
    logger.LogInformation("Runtime: {Runtime}", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

    // Opt-in, stdio-safe tracing of MCP tool invocations (RoselineMCP:EnableDiagnosticLogging).
    // When disabled (the default), no ActivityListener is registered against
    // RoselineDiagnostics.ActivitySource, so the per-tool Activity spans the tools create are
    // never sampled and cost next to nothing. When enabled, span start/stop is logged through
    // this same ILogger pipeline (already routed to stderr) — never to stdout, and never over
    // the network. See RoselineDiagnostics for details.
    var diagnosticsOptions = host.Services.GetRequiredService<IOptions<RoselineMcpOptions>>().Value;
    using var activityListener = diagnosticsOptions.EnableDiagnosticLogging
        ? RoselineDiagnostics.RegisterStderrListener(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RoselineMCP.Diagnostics"))
        : null;

    if (diagnosticsOptions.EnableDiagnosticLogging)
    {
        logger.LogInformation("Diagnostic tracing enabled (RoselineMCP:EnableDiagnosticLogging=true): per-tool invocation spans will be logged to stderr.");
    }

    // Run the application
    await host.RunAsync();

    logger.LogInformation("RoselineMCP server stopped gracefully");
    return 0;
}
catch (Exception ex)
{
    // Log fatal errors to stderr
    await Console.Error.WriteLineAsync($"Fatal error: {ex.Message}");
    await Console.Error.WriteLineAsync($"Stack trace: {ex.StackTrace}");
    return 1;
}

// Create the host builder with all configurations
static IHostBuilder CreateHostBuilder(string[] args) =>
    Host.CreateDefaultBuilder(args)
        // Anchor the content root to the directory the server binary lives in, so the packaged
        // appsettings.json / appsettings.{Environment}.json are loaded from the install location
        // (AppContext.BaseDirectory) — never from whatever directory the process was started in.
        // A target repository's own appsettings.json must not reconfigure this server, and a
        // globally installed dotnet tool must still find its packaged settings. CWD-based
        // behavior elsewhere (e.g. ProjectLoader's project auto-discovery) uses
        // Directory.GetCurrentDirectory() directly and is unaffected by the content root.
        .UseContentRoot(AppContext.BaseDirectory)
        .ConfigureHostConfiguration(host =>
        {
            // CreateDefaultBuilder adds the appsettings files with reloadOnChange: true, which
            // spins up FileSystemWatchers a one-shot stdio server never needs. This documented
            // host setting makes it pass reloadOnChange: false instead.
            host.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["hostBuilder:reloadConfigOnChange"] = "false",
            });
        })
        .ConfigureAppConfiguration((_, config) =>
        {
            // appsettings.json and appsettings.{Environment}.json are already added by
            // CreateDefaultBuilder, resolved against the content root set above — don't add them
            // again here. Only append the ROSELINE_-prefixed environment variables and the
            // command-line arguments (last, so they keep the highest precedence).
            config
                .AddEnvironmentVariables(prefix: "ROSELINE_")
                .AddCommandLine(args);
        })
        .ConfigureServices((context, services) =>
        {
            // Bind RoselineMCP:* configuration (e.g. DefaultTimeout) so it can be injected
            // into MCP tool methods via IOptions<RoselineMcpOptions>.
            services.Configure<RoselineMcpOptions>(context.Configuration.GetSection("RoselineMCP"));

            // Configure MCP Server
            services
                .AddMcpServer(options => options.ServerInstructions = RoselineMCP.RoselineToolGuidance.Instructions)
                .WithStdioServerTransport()
                .WithToolsFromAssembly();

            // Add Core Services
            services.AddSingleton<IMSBuildService, MSBuildService>();
            // Bundled analyzer/fixer assemblies (Roslynator) — loaded once, shared by the
            // code fix provider factory (fixers) and the diagnostic computation (analyzers).
            services.AddSingleton<IAnalyzerCatalog, AnalyzerCatalog>();
            // Compiler + analyzer diagnostics for AnalyzeSolution/ListDiagnostics/ApplyFixes
            // (RoselineMCP:RunAnalyzers=false makes it compiler-only).
            services.AddSingleton<IDiagnosticComputationService, DiagnosticComputationService>();
            services.AddSingleton<IDiagnosticFilterService, DiagnosticFilterService>();
            services.AddSingleton<ICodeFixProviderFactory, CodeFixProviderFactory>();
            services.AddSingleton<IDiffService, DiffService>();
            // Navigation/edit project loading: IProjectLoader resolves to the caching decorator
            // wrapping the real loader, so the MSBuild workspace is reused across tool calls
            // (fingerprint-invalidated on any file change; RoselineMCP:WorkspaceCache=false bypasses).
            services.AddSingleton<ProjectLoader>();
            services.AddSingleton<IProjectLoader>(sp => new CachingProjectLoader(
                sp.GetRequiredService<ProjectLoader>(),
                sp.GetRequiredService<IOptions<RoselineMcpOptions>>(),
                sp.GetRequiredService<ILogger<CachingProjectLoader>>()));

            // Add Business Services
            services.AddSingleton<ISolutionAnalyzerService, SolutionAnalyzerService>();
            services.AddSingleton<ICodeFixService, CodeFixService>();
            services.AddSingleton<IPatchService, PatchService>();
            services.AddSingleton<ICodeNavigationService, CodeNavigationService>();
            services.AddSingleton<ICodeEditService, CodeEditService>();
        })
        .ConfigureLogging((context, logging) =>
        {
            // Clear default providers
            logging.ClearProviders();

            // Add console logging with stderr output
            logging.AddConsole(options =>
            {
                // MCP servers should output all logs to stderr
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });

            // Set minimum log levels
            if (context.HostingEnvironment.IsDevelopment())
            {
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddFilter("RoselineMCP", LogLevel.Debug);
            }
            else
            {
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddFilter("RoselineMCP", LogLevel.Information);
            }

            logging.AddFilter("Microsoft", LogLevel.Warning);
            logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
        })
        .UseConsoleLifetime(options =>
        {
            // Configure console lifetime options
            options.SuppressStatusMessages = true;
        });

// Minimal Program class for dependency injection
[ExcludeFromCodeCoverage]
public partial class Program
{
    // Configure global exception handlers
    static Program()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            Console.Error.WriteLine($"Unhandled exception: {exception?.Message ?? "Unknown error"}");
            if (exception != null)
            {
                Console.Error.WriteLine($"Stack trace: {exception.StackTrace}");
            }
            Environment.Exit(1);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            foreach (var ex in e.Exception.Flatten().InnerExceptions)
            {
                Console.Error.WriteLine($"Unobserved task exception: {ex.Message}");
                Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        };
    }
}