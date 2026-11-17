using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace JAz.CloudShell.Tests;

[Collection("CloudShell transport")]
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

        Assert.EndsWith("\r", command);
        Assert.DoesNotContain('\n', command);
        Assert.Contains("| pwsh -NoLogo -NoProfile -s 2>&1", command);
        Assert.Contains("JZSTARTCAFE", command);
        Assert.Contains("\"JZSTOPCAFE\"", command);

        // The terminal echoes the typed command; only the printf output may contain the markers.
        Assert.DoesNotContain("~JZRCAFE~", command);
        Assert.DoesNotContain("~JZCCAFE~", command);
    }

    [Fact]
    public void ConnectionProgress_AdvancesByPhaseAndNeverMovesBackwards()
    {
        var percent = -1;
        var seen = new List<int>();
        foreach (var message in new[]
        {
            "Acquiring an Azure Resource Manager token with the default Azure credential chain.",
            "Reading Cloud Shell user settings.",
            "Cloud Shell rejected the Azure PowerShell client; requesting a compatible token for the same account and tenant.",
            "Acquiring an Azure Resource Manager token with an account-verified Cloud Shell credential.",
            "Reading Cloud Shell user settings.",
            "Requesting a Cloud Shell console.",
            "Creating a bash terminal.",
            "Connecting to the Cloud Shell terminal WebSocket.",
            "Waiting for the Cloud Shell bash prompt.",
            "Starting PowerShell in server mode (pwsh -s).",
        })
        {
            percent = NewJAzCloudShellSessionCommand.AdvancePercent(percent, message);
            seen.Add(percent);
        }

        Assert.Equal([5, 15, 15, 15, 15, 25, 40, 50, 60, 75], seen);
    }
}
