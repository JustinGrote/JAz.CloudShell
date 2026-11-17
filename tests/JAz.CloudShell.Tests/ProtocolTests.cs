using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace JAz.CloudShell.Tests;

public class ProtocolTests
{
    [Fact]
    public void BuildWebSocketUri_DirectEndpoint()
    {
        var uri = CloudShellClient.BuildWebSocketUri(new Uri("https://ux.console.azure.com/c/cc-1234/"), "abc", null);
        Assert.Equal("wss://ux.console.azure.com/c/cc-1234/terminals/abc", uri.AbsoluteUri);
    }

    [Fact]
    public void BuildWebSocketUri_RelayEndpoint()
    {
        var uri = CloudShellClient.BuildWebSocketUri(
            new Uri("https://ccon-prod-westus-aci-01.servicebus.windows.net/cc-AAAA-BBBB/"),
            "t1",
            "https://ccon-prod-westus-aci-01.servicebus.windows.net/cc-AAAA-BBBB/terminals/t1?x=1");
        Assert.Equal("wss://ccon-prod-westus-aci-01.servicebus.windows.net/$hc/cc-AAAA-BBBB/terminals/t1", uri.AbsoluteUri);
    }

    [Fact]
    public void BuildWebSocketUri_RelayWithoutSocketUriFails() =>
        Assert.Throws<CloudShellException>(() => CloudShellClient.BuildWebSocketUri(new Uri("https://x.servicebus.windows.net/cc/"), "t", null));

    [Theory]
    [InlineData("https://evil.example.com/cc/")]
    [InlineData("http://ux.console.azure.com/cc/")]
    public void ValidateServiceUri_RejectsUnexpectedEndpoints(string value) =>
        Assert.Throws<CloudShellException>(() => CloudShellClient.ValidateServiceUri(value, Uri.UriSchemeHttps, "Test"));

    [Fact]
    public void CountCursorQueries_DetectsQueriesSplitAcrossMessages()
    {
        var tail = string.Empty;
        Assert.Equal(1, CloudShellBridgeChannel.CountCursorQueries(ref tail, "PS> \e[6n\e["));
        Assert.Equal(1, CloudShellBridgeChannel.CountCursorQueries(ref tail, "6nabc"));
        Assert.Equal(2, CloudShellBridgeChannel.CountCursorQueries(ref tail, "\e[6n\e[6n"));
        Assert.Equal(0, CloudShellBridgeChannel.CountCursorQueries(ref tail, "6n"));
    }

    [Fact]
    public void BuildLaunchCommand_StartsServerModeWithoutEchoingMarkers()
    {
        var command = Bootstrapper.BuildLaunchCommand("CAFE");
        var script = Regex.Match(command, "bash -c '([^']*)';").Groups[1].Value;

        Assert.EndsWith("\r", command);
        Assert.DoesNotContain('\n', command);
        Assert.Contains("-NoLogo -NoProfile -s 2>&1", script);
        Assert.Contains("JZSTARTCAFE", script);
        Assert.Contains("\"JZSTOPCAFE\"", script);

        // The terminal echoes the typed command; only the printf output may contain the markers.
        Assert.DoesNotContain("~JZRCAFE~", command);
        Assert.DoesNotContain("~JZCCAFE~", command);
    }
}