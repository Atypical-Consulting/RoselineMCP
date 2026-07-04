using System.Reflection;
using Shouldly;

namespace RoselineMCP.Tests.Protocol;

/// <summary>
/// Asserts what the server introduces itself as during the MCP <c>initialize</c> handshake.
/// Regression guard: the SDK's default <c>serverInfo.version</c> is the AssemblyVersion, which
/// MinVer pins to <c>{Major}.0.0.0</c> — a released 2.1.0 build reported itself as "2.0.0.0".
/// The handshake must advertise the real package semver (InformationalVersion without the
/// <c>+buildmetadata</c> suffix) instead.
/// </summary>
[Collection(McpProtocolCollection.Name)]
public class ServerInfoTests : McpProtocolTestBase
{
    [Fact]
    public void Initialize_Reports_The_Package_Semver_Not_The_AssemblyVersion()
    {
        var serverAssembly = typeof(RoselineServerInfo).Assembly;
        var informational = serverAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // MinVer always stamps an informational version on the built server assembly; if this
        // ever goes missing the whole premise of the version fix needs revisiting.
        informational.ShouldNotBeNullOrWhiteSpace();

        var serverInfo = Client.ServerInfo;

        serverInfo.Name.ShouldBe("RoselineMCP");
        serverInfo.Version.ShouldBe(RoselineServerInfo.ResolveVersion(informational, null));
        serverInfo.Version.ShouldNotContain("+"); // build metadata is stripped
        // The four-part AssemblyVersion ({Major}.0.0.0 under MinVer) must never leak to the wire.
        serverInfo.Version.ShouldNotBe(serverAssembly.GetName().Version!.ToString());
    }

    [Theory]
    [InlineData("2.1.0", "2.1.0")]
    [InlineData("2.1.0+abc1234", "2.1.0")]
    [InlineData("2.2.0-alpha.0.5+abc1234.dirty", "2.2.0-alpha.0.5")]
    [InlineData("2.2.0-alpha.0.5", "2.2.0-alpha.0.5")]
    public void ResolveVersion_Strips_Build_Metadata_From_The_Informational_Version(
        string informational, string expected)
    {
        RoselineServerInfo.ResolveVersion(informational, new Version(2, 0, 0, 0)).ShouldBe(expected);
    }

    [Fact]
    public void ResolveVersion_Falls_Back_To_The_Assembly_Version_When_No_Informational_Version_Exists()
    {
        RoselineServerInfo.ResolveVersion(null, new Version(2, 1, 0)).ShouldBe("2.1.0");
        RoselineServerInfo.ResolveVersion(" ", new Version(2, 1, 0)).ShouldBe("2.1.0");
        RoselineServerInfo.ResolveVersion(null, null).ShouldBe("0.0.0");
    }
}
