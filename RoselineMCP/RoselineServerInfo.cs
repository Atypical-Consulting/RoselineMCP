using System.Reflection;
using ModelContextProtocol.Protocol;

namespace RoselineMCP;

/// <summary>
/// Builds the MCP <c>serverInfo</c> advertised during the <c>initialize</c> handshake.
/// </summary>
/// <remarks>
/// Left to its own devices, the MCP SDK defaults <c>serverInfo.version</c> to the entry assembly's
/// <em>AssemblyVersion</em> — which MinVer pins to <c>{Major}.0.0.0</c> for binary-compatibility
/// reasons — so a released 2.1.0 build would introduce itself to clients as <c>"2.0.0.0"</c>.
/// This helper resolves the real package semver from the assembly's
/// <see cref="AssemblyInformationalVersionAttribute"/> (where MinVer stamps the full version,
/// e.g. <c>2.1.0+abc1234</c>), stripping any <c>+buildmetadata</c> suffix, and falls back to the
/// assembly version only when no informational version is present.
/// </remarks>
public static class RoselineServerInfo
{
    /// <summary>Creates the <see cref="Implementation"/> the server reports as <c>serverInfo</c>.</summary>
    public static Implementation Create()
    {
        var assembly = typeof(RoselineServerInfo).Assembly;
        var assemblyName = assembly.GetName();

        return new Implementation
        {
            Name = assemblyName.Name ?? "RoselineMCP",
            Version = ResolveVersion(
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                assemblyName.Version),
        };
    }

    /// <summary>
    /// Resolves the version string to advertise: the informational version with any semver
    /// build-metadata suffix (<c>+…</c>) stripped, or the assembly version when no informational
    /// version is stamped.
    /// </summary>
    public static string ResolveVersion(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataStart = informationalVersion.IndexOf('+');
            return metadataStart >= 0 ? informationalVersion[..metadataStart] : informationalVersion;
        }

        return assemblyVersion?.ToString() ?? "0.0.0";
    }
}
