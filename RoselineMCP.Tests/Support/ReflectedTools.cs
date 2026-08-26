using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace RoselineMCP.Tests.Support;

/// <summary>
/// The single reflection over the server's <c>[McpServerTool]</c> methods, shared by
/// <see cref="Protocol.ToolDescriptionContractTests"/> (the description contract) and
/// <see cref="Release.WebsiteToolsDataContractTests"/> (the <c>website/src/data/tools.ts</c> pin) so
/// the two can never disagree about what the tool set <em>is</em> — extracted for #208, which pins
/// the website array to this same enumeration one link upstream of #206's headings-derive-from-the-array
/// fix.
/// </summary>
public static class ReflectedTools
{
    /// <summary>
    /// Every <c>[McpServerTool]</c> method in the server assembly, in a stable order.
    /// </summary>
    public static IEnumerable<MethodInfo> Methods() =>
        typeof(RoselineServerInfo).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(m => m.Name, StringComparer.Ordinal);

    /// <summary>
    /// The MCP wire name <paramref name="method"/> is exposed under: the <c>Name</c> named argument
    /// on <see cref="McpServerToolAttribute"/> when one is set, otherwise the SDK's own default —
    /// confirmed against <c>ModelContextProtocol.Core</c>, which derives it via
    /// <c><see cref="JsonNamingPolicy.SnakeCaseLower"/>.ConvertName(methodName)</c> — the same
    /// conversion <c>ToolListingTests</c> pins end-to-end through a real <c>tools/list</c> call.
    /// None of RoselineMCP's tools set <c>Name</c> explicitly today, so every wire name here is
    /// currently the derived form (e.g. <c>GetSymbolAtPosition</c> → <c>get_symbol_at_position</c>),
    /// but both paths are honoured so a future explicit override stays correct.
    /// </summary>
    public static string WireName(MethodInfo method) =>
        method.GetCustomAttribute<McpServerToolAttribute>()?.Name
            ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(method.Name);

    /// <summary>
    /// The set of wire names for every reflected tool — what <c>website/src/data/tools.ts</c> is
    /// pinned against.
    /// </summary>
    public static IReadOnlySet<string> WireNames() =>
        Methods().Select(WireName).ToHashSet(StringComparer.Ordinal);
}
