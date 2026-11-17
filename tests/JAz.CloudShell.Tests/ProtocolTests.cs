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
    public async Task ConsoleCache_ReusesConsoleAndTokenAndRecoversWhenConsoleIsGone()
    {
        CloudShellConsoleCache.Clear();
        var requests = new List<string>();
        var tokens = 0;
        var failNextTerminal = false;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.Method.Method);
            if (request.Method == HttpMethod.Post && failNextTerminal)
            {
                failNextTerminal = false;
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method.Method switch
                {
                    "PUT" => """{"properties":{"uri":"https://ux.console.azure.com/c/cc-1"}}""",
                    "POST" => """{"id":"t1"}""",
                    _ => "{}",
                }),
            };
        }));
        ValueTask<Azure.Core.AccessToken> GetToken(CancellationToken ct)
        {
            tokens++;
            return ValueTask.FromResult(new Azure.Core.AccessToken("token", DateTimeOffset.UtcNow.AddHours(1)));
        }

        Task<(Uri WebSocketUri, bool Reused)> Create() => CloudShellConsoleCache.CreateTerminalAsync("tenant", GetToken, http, 80, 24, _ => { }, CancellationToken.None);
        try
        {
            var first = await Create();
            Assert.Equal(("wss://ux.console.azure.com/c/cc-1/terminals/t1", false), (first.WebSocketUri.AbsoluteUri, first.Reused));
            Assert.Equal(["GET", "PUT", "POST"], requests);

            requests.Clear();
            Assert.True((await Create()).Reused);
            Assert.Equal(["POST"], requests);

            requests.Clear();
            failNextTerminal = true;
            Assert.False((await Create()).Reused);
            Assert.Equal(["POST", "GET", "PUT", "POST"], requests);
            Assert.Equal(2, tokens);
        }
        finally
        {
            CloudShellConsoleCache.Clear();
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public void ConnectionProgress_AdvancesByPhaseAndNeverMovesBackwards()
    {
        var percent = -1;
        var seen = new List<int>();
        foreach (var message in new[]
        {
            "Reusing the existing Cloud Shell console.",
            "Could not reuse the Cloud Shell console (gone); requesting it again.",
            "Acquiring an Azure Resource Manager token with DefaultAzureCredential.",
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

        Assert.Equal([30, 30, 30, 30, 30, 40, 50, 60, 75], seen);
    }
}
