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
    public void FrameParser_HandlesSplitCoalescedAndForeignText()
    {
        var parser = new FrameParser("N1");
        var a = FrameParser.Encode("N1", 'D', "hello"u8);
        var b = FrameParser.Encode("N1", 'C', ReadOnlySpan<byte>.Empty);
        var echo = FrameParser.Encode("N1", 'd', "echo"u8);
        var other = FrameParser.Encode("N2", 'D', "other"u8);
        var stream = "PS> noise\r\n" + other + a + echo + b;

        var frames = new List<(char Kind, byte[] Payload)>();
        foreach (var piece in stream.Chunk(3))
        {
            frames.AddRange(parser.Feed(new string(piece)));
        }

        Assert.Equal(['D', 'd', 'C'], frames.Select(f => f.Kind));
        Assert.Equal("hello", Encoding.UTF8.GetString(frames[0].Payload));
        Assert.Empty(frames[2].Payload);
    }

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
    public void FrameParser_RejectsCorruptPayload() =>
        Assert.Throws<InvalidDataException>(() => new FrameParser("N").Feed("~JZN|D|not base64!~").ToList());

    [Fact]
    public void ProbeParsing_AcceptsPrereleaseVersions()
    {
        var match = new Regex(@"~JZPab\|(\d+)\|([0-9][^|~\s]*)\|([0-9][^|~\s]*)~").Match("~JZPab|4242|7.6.1-preview.2|10.0.3~");
        var result = Bootstrapper.ParseProbe(match, "ab");
        Assert.Equal(4242, result.ProcessId);
        Assert.Equal(new Version(7, 6, 1), result.PSVersion);
        Bootstrapper.ValidateRemoteVersion(result);
    }

    [Theory]
    [InlineData("7.5.4", "10.0.0")]
    [InlineData("7.6.0", "9.0.10")]
    public void ValidateRemoteVersion_RejectsOldRuntimes(string ps, string net) =>
        Assert.Throws<CloudShellException>(() => Bootstrapper.ValidateRemoteVersion(new BootstrapResult(1, Version.Parse(ps), Version.Parse(net), "n")));

    [Fact]
    public void EncodeHelper_FillsPlaceholdersAndStripsComments()
    {
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(Bootstrapper.EncodeHelper("CAFE", 77)));
        Assert.Contains("$n = 'CAFE'", script);
        Assert.Contains("$t = 77", script);
        Assert.DoesNotContain("__NONCE__", script);
        Assert.DoesNotContain("# Temporary bridge helper", script);
    }
}
