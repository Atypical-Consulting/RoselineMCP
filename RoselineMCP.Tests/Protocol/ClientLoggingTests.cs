using System.Text.Json;
using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Protocol;

/// <summary>
/// Verifies that a tool failure is surfaced to the client's log stream as an MCP
/// <c>notifications/message</c>, carrying the same correlation ID that appears in the error
/// envelope — so an operator watching the client's logs can tie a reported failure back to the
/// full server-side log entry.
/// </summary>
[Collection(McpProtocolCollection.Name)]
public class ClientLoggingTests
{
    /// <summary>
    /// The newest MCP revision on which the Logging feature is still live, so this test drives the
    /// pipeline it is actually covering.
    /// </summary>
    /// <remarks>
    /// SEP-2577 deprecated Logging in <c>2026-07-28</c>: <c>logging/setLevel</c> is rejected, and a
    /// server must not emit <c>notifications/message</c> for a request that did not carry the
    /// <c>io.modelcontextprotocol/logLevel</c> <c>_meta</c> field. The SDK's own
    /// <see cref="ModelContextProtocol.Client.McpClient"/> has no API to set that field — on a
    /// <c>2026-07-28</c> session it injects per-request <c>_meta</c> and strips the key outright — so a
    /// client cannot opt into logging at all there, and this pipeline is inert by construction. Pinning
    /// keeps the coverage honest rather than asserting behavior the current protocol cannot produce.
    /// </remarks>
    private const string LegacyLoggingProtocolVersion = "2025-11-25";

    [Fact]
    public async Task Failing_Tool_Emits_A_Client_Log_Notification_Carrying_The_CorrelationId()
    {
        var analyzer = A.Fake<ISolutionAnalyzerService>();
        A.CallTo(() => analyzer.AnalyzeSolutionAsync(
                A<string>._, A<string?>._, A<string?>._, A<string?>._, A<string?>._, A<int>._,
                A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Throws(new FileNotFoundException("Solution file not found"));

        await using var host = await McpProtocolTestHost.StartAsync(services =>
        {
            services.AddSingleton(analyzer);
            services.AddSingleton(A.Fake<ICodeFixService>());
            services.AddSingleton(A.Fake<ICodeNavigationService>());
            services.AddSingleton(A.Fake<ICodeEditService>());
            services.AddSingleton<IDiffService, DiffService>();
            services.AddSingleton<IPatchService, PatchService>();
        }, protocolVersion: LegacyLoggingProtocolVersion);

        var received = new List<string>();
        var gotOne = new TaskCompletionSource();
        // Deprecated by SEP-2577 (see LegacyLoggingProtocolVersion); this test pins the revision where
        // the feature is live precisely so it keeps exercising the real pipeline.
#pragma warning disable MCP9005
        host.Client.RegisterNotificationHandler(NotificationMethods.LoggingMessageNotification, (notification, _) =>
        {
            if (notification.Params is { } p)
            {
                received.Add(p.ToJsonString());
                gotOne.TrySetResult();
            }
            return default;
        });

        // The client must opt into logging for the server to send notifications.
        await host.Client.SetLoggingLevelAsync(LogLevel.Information);
#pragma warning restore MCP9005

        var result = await host.Client.CallToolAsync("analyze_solution", new Dictionary<string, object?>
        {
            ["pathOrGit"] = "missing.sln",
        });

        // Correlation ID from the error envelope the caller receives.
        var envelope = JsonDocument.Parse((result.Content[0] as TextContentBlock)!.Text).RootElement;
        var correlationId = envelope.GetProperty("error").GetProperty("correlationId").GetString();
        correlationId.ShouldNotBeNullOrWhiteSpace();

        // The same correlation ID must appear in a client-facing log notification.
        await gotOne.Task.WaitAsync(TimeSpan.FromSeconds(10));
        received.ShouldContain(json => json.Contains(correlationId!));
    }
}
