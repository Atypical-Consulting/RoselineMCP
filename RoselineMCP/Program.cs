using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Diagnostics;
using RoselineMCP.Guard;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;

// The `guard` verb is a SHORT-LIVED CLIENT, not the server: the agent harness runs it once per file
// write, it asks the already-running server for a verdict over the local guard endpoint, and it
// exits. It is intercepted here, before any host is built, because building the MCP host would
// claim stdio — the very channel this process must leave alone (stdout is the hook's JSON result).
if (args.Length > 0 && string.Equals(args[0], "guard", StringComparison.OrdinalIgnoreCase))
{
    if (args.Contains("--print-hook", StringComparer.OrdinalIgnoreCase))
    {
        return GuardClient.PrintHook(Console.Out);
    }

    return await GuardClient.RunAsync(
        Console.In, Console.Out, Console.Error, ReadGuardOptions());
}

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

    if (diagnosticsOptions.ConfirmDestructiveWrites
        && diagnosticsOptions.ConfirmDestructiveWritesTimeout <= 0)
    {
        // The documented escape hatch back to an indefinite wait. Worth one line of stderr:
        // its failure mode is a write tool that never returns, which leaves no error and no
        // log of its own to diagnose it by — this is the only breadcrumb.
        logger.LogWarning(
            "Write confirmation is unbounded (RoselineMCP:ConfirmDestructiveWritesTimeout={Timeout}): a client that never answers the confirmation will block the write tool indefinitely.",
            diagnosticsOptions.ConfirmDestructiveWritesTimeout);
    }

    if (!diagnosticsOptions.ConfirmDestructiveWrites)
    {
        // Say so once, loudly: from here on, a previewOnly:false call writes with no human in the
        // loop, and the response is indistinguishable from one a human approved. An operator
        // reading stderr should be able to tell which deployment they are looking at.
        logger.LogWarning(
            "Write confirmation disabled (RoselineMCP:ConfirmDestructiveWrites=false): the write tools will NOT ask the client to confirm before writing; an explicit previewOnly:false is the only remaining guard.");
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

// Guard configuration for the `guard` verb, read from the same places the host reads it from —
// appsettings.json next to the binary (never the process working directory, which for the hook is
// the agent's repository) plus the ROSELINE_ environment variables. Bound by hand rather than with
// Get<T>() to keep this path free of the configuration-binder dependency.
static RoselineMcpOptions ReadGuardOptions()
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables(prefix: "ROSELINE_")
        .Build();

    var options = new RoselineMcpOptions();

    if (bool.TryParse(configuration["RoselineMCP:Guard"], out var guard))
    {
        options.Guard = guard;
    }

    var endpoint = configuration["RoselineMCP:GuardEndpoint"];
    if (!string.IsNullOrWhiteSpace(endpoint))
    {
        options.GuardEndpoint = endpoint;
    }

    if (int.TryParse(configuration["RoselineMCP:GuardTimeout"], out var timeout))
    {
        options.GuardTimeout = timeout;
    }

    return options;
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
                .AddMcpServer(options =>
                {
                    options.ServerInstructions = RoselineMCP.RoselineToolGuidance.Instructions;
                    // Explicit serverInfo: the SDK's default takes the AssemblyVersion, which
                    // MinVer pins to {Major}.0.0.0 — so a released 2.1.0 build would report
                    // "2.0.0.0" in the initialize handshake. RoselineServerInfo advertises the
                    // real package semver (InformationalVersion minus +buildmetadata) instead.
                    options.ServerInfo = RoselineMCP.RoselineServerInfo.Create();
                })
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
            // Project loading for the navigation, edit, and diagnostics/fix tools: IProjectLoader
            // resolves to the caching decorator wrapping the real loader, so the MSBuild workspace
            // is reused across tool calls (fingerprint-invalidated on any file change;
            // RoselineMCP:WorkspaceCache=false bypasses).
            services.AddSingleton<ProjectLoader>();
            services.AddSingleton<IProjectLoader>(sp => new CachingProjectLoader(
                sp.GetRequiredService<ProjectLoader>(),
                sp.GetRequiredService<IOptions<RoselineMcpOptions>>(),
                sp.GetRequiredService<ILogger<CachingProjectLoader>>()));

            // Add Business Services
            // Compiler-only, on purpose: verification is a build gate, not a style gate. Handing it
            // the analyzer-aware DiagnosticComputationService would cost several times a bare
            // compile and start refusing writes over RCS/StyleCop opinions.
            services.AddSingleton<IVerificationService>(sp => new VerificationService(
                sp.GetRequiredService<ILogger<VerificationService>>(),
                DiagnosticComputationService.CompilerOnly));
            services.AddSingleton<ISolutionAnalyzerService, SolutionAnalyzerService>();
            services.AddSingleton<ICodeFixService, CodeFixService>();
            services.AddSingleton<IPatchService, PatchService>();
            services.AddSingleton<ICodeNavigationService, CodeNavigationService>();
            services.AddSingleton<ICodeEditService, CodeEditService>();

            // Compile guard (RoselineMCP:Guard, default false). Registered only when asked for, so a
            // default install neither holds guard state nor opens a local socket. Read straight off
            // the configuration rather than through IOptions: this decides whether the hosted service
            // is registered at all, which has to happen while the container is still being built.
            if (bool.TryParse(context.Configuration["RoselineMCP:Guard"], out var guardEnabled) && guardEnabled)
            {
                services.AddSingleton<IGuardService, GuardService>();
                services.AddHostedService<GuardEndpoint>();
            }
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