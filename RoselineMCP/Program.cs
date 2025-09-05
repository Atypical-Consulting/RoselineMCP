using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        .ConfigureAppConfiguration((context, config) =>
        {
            // Add configuration sources
            config
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true)
                .AddEnvironmentVariables(prefix: "ROSELINE_")
                .AddCommandLine(args);
        })
        .ConfigureServices((context, services) =>
        {
            // Configure MCP Server
            services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithToolsFromAssembly();

            // Add Core Services
            services.AddSingleton<IMSBuildService, MSBuildService>();
            services.AddSingleton<IDiagnosticFilterService, DiagnosticFilterService>();
            services.AddSingleton<ICodeFixProviderFactory, CodeFixProviderFactory>();
            services.AddSingleton<IDiffService, DiffService>();

            // Add Business Services
            services.AddSingleton<ISolutionAnalyzerService, SolutionAnalyzerService>();
            services.AddSingleton<ICodeFixService, CodeFixService>();
            services.AddSingleton<IPatchService, PatchService>();
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
public partial class Program
{
    // Configure global exception handlers
    static Program()
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            Console.Error.WriteLine($"Unhandled exception: {exception?.Message ?? "Unknown error"}");
            if (exception != null)
            {
                Console.Error.WriteLine($"Stack trace: {exception.StackTrace}");
            }
            Environment.Exit(1);
        };

        TaskScheduler.UnobservedTaskException += (sender, e) =>
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