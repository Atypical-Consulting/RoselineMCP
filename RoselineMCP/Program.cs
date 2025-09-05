using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(consoleLogOptions =>
{
    // Configure all logs to go to stderr
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

builder.Services.AddSingleton<ISolutionAnalyzerService, SolutionAnalyzerService>();
builder.Services.AddSingleton<ICodeFixService, CodeFixService>();
builder.Services.AddSingleton<IPatchService, PatchService>();

await builder.Build().RunAsync();