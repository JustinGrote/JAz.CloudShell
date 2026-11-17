using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace JAz.CloudShell.Tests;

/// <summary>
/// A local stand-in for the Cloud Shell terminal WebSocket: each connection is backed by a real pwsh process whose
/// stdin/stdout are relayed over the socket. This exercises the real bootstrap, server-mode pwsh, and PSRP.
/// </summary>
internal sealed class FakeTerminalServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Process> _processes = [];
    private readonly StringBuilder _stderr = new();

    public FakeTerminalServer()
    {
        var port = GetFreePort();
        Uri = new Uri($"ws://127.0.0.1:{port}/terminals/1");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public Uri Uri { get; }

    public int? LastProcessId { get; private set; }

    /// <summary>Number of server-mode processes the client has stopped with the stop line.</summary>
    public int StoppedServers => _stoppedServers;

    private int _stoppedServers;

    /// <summary>Number of terminal WebSockets the client has closed.</summary>
    public int ClosedConnections => _closedConnections;

    private int _closedConnections;

    public string StdErr
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
            _ = Task.Run(() => ServeAsync(ws));
        }
    }

    /// <summary>
    /// Emulates the Cloud Shell terminal: an interactive bash prompt that echoes input until the launch line arrives,
    /// then the bash launcher (raw TTY, discard input until the start line, forward lines to <c>pwsh -s</c> until the
    /// stop line, print the exit marker). The server-mode pwsh is real.
    /// </summary>
    private async Task ServeAsync(WebSocket ws)
    {
        var sendLock = new SemaphoreSlim(1, 1);
        async Task SendAsync(byte[] bytes)
        {
            await sendLock.WaitAsync();
            try
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            finally
            {
                sendLock.Release();
            }
        }

        Process? process = null;
        Task? relay = null;
        try
        {
            // Look like Cloud Shell bash at an idle prompt.
            await SendAsync("\e]0;test\a\e[?2004htest [ ~ ]$ "u8.ToArray());

            var buffer = new byte[16384];
            var pending = new StringBuilder();
            string? nonce = null;
            var forwarding = false;
            var stopped = false;
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Interlocked.Increment(ref _closedConnections);
                    break;
                }

                if (stopped)
                {
                    // Back at the bash prompt; ignore input until the client closes the socket.
                    continue;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (nonce is null)
                {
                    // Interactive prompt: readline echoes typed text; a stray cursor report is dropped.
                    text = text.Replace("\e[1;1R", string.Empty, StringComparison.Ordinal);
                    await SendAsync(Encoding.UTF8.GetBytes(text));
                    pending.Append(text);
                    var line = pending.ToString();
                    var enter = line.IndexOf('\r');
                    if (enter < 0)
                    {
                        continue;
                    }

                    var match = System.Text.RegularExpressions.Regex.Match(line[..enter], @"printf ""~JZ%s%s~\\n"" R ([0-9A-F]+);");
                    Assert.True(match.Success, $"Unexpected command: {line}");
                    nonce = match.Groups[1].Value;
                    pending.Clear();
                    pending.Append(line[(enter + 1)..].Replace('\r', '\n'));

                    var psi = new ProcessStartInfo("pwsh", "-NoLogo -NoProfile -s")
                    {
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };
                    process = Process.Start(psi)!;
                    lock (_processes)
                    {
                        _processes.Add(process);
                    }

                    LastProcessId = process.Id;
                    var proc = process;
                    var exitMarker = Encoding.UTF8.GetBytes($"~JZC{nonce}~\n");
                    relay = Task.Run(async () =>
                    {
                        // stdout and stderr share the terminal (2>&1 | cat).
                        var stderr = Task.Run(async () =>
                        {
                            string? l;
                            while ((l = await proc.StandardError.ReadLineAsync()) is not null)
                            {
                                lock (_stderr)
                                {
                                    _stderr.AppendLine(l);
                                }

                                await SendAsync(Encoding.UTF8.GetBytes(l + "\n"));
                            }
                        });
                        var outBuffer = new byte[8192];
                        int read;
                        while ((read = await proc.StandardOutput.BaseStream.ReadAsync(outBuffer)) > 0)
                        {
                            await SendAsync(outBuffer[..read]);
                        }

                        await stderr;
                        await SendAsync(exitMarker);
                    });

                    // A cursor query arriving just before raw mode, whose report the input filter must discard.
                    await SendAsync(Encoding.UTF8.GetBytes($"\e[6n~JZR{nonce}~\n"));
                    text = string.Empty;
                }
                else
                {
                    pending.Append(text);
                }

                // Raw mode: lines are LF-terminated and not echoed.
                var all = pending.ToString();
                var last = all.LastIndexOf('\n');
                if (last < 0)
                {
                    continue;
                }

                pending.Clear();
                pending.Append(all[(last + 1)..]);
                var stdin = process!.StandardInput;
                foreach (var l in all[..last].Split('\n'))
                {
                    if (!forwarding)
                    {
                        forwarding = l.Contains($"JZSTART{nonce}", StringComparison.Ordinal);
                        continue;
                    }

                    if (l == $"JZSTOP{nonce}")
                    {
                        stdin.Close();
                        stopped = true;
                        Interlocked.Increment(ref _stoppedServers);
                        break;
                    }

                    await stdin.WriteAsync(l + "\n");
                }

                if (!stopped)
                {
                    await stdin.FlushAsync();
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (relay is not null)
            {
                await Task.WhenAny(relay, Task.Delay(5000));
            }

            try
            {
                process?.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            ws.Dispose();
        }
    }
    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        lock (_processes)
        {
            foreach (var p in _processes)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
        }
    }
}

[Collection("CloudShell transport")]
public sealed class EndToEndTests : IDisposable
{
    private readonly FakeTerminalServer _server = new();
    private readonly PowerShell _ps;

    public EndToEndTests()
    {
        var iss = InitialSessionState.CreateDefault2();
        iss.ImportPSModule(["Az.Accounts"]);
        iss.Commands.Add(new SessionStateCmdletEntry("New-JAzCloudShellSession", typeof(NewJAzCloudShellSessionCommand), null));
        _ps = PowerShell.Create(iss);
        NewJAzCloudShellSessionCommand.WebSocketUriOverride = _server.Uri;
    }

    private Collection<PSObject> Run(string script)
    {
        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        _ps.AddScript(script);
        var output = _ps.Invoke();
        var errors = string.Join(Environment.NewLine, _ps.Streams.Error.Select(e => e.ToString()));
        Assert.True(_ps.Streams.Error.Count == 0, $"{errors}{Environment.NewLine}Remote stderr:{Environment.NewLine}{_server.StdErr}");
        return output;
    }

    [Fact]
    public void InvokeCommand_RunsInTheTerminalPowerShellProcess()
    {
        Run("$global:s = New-JAzCloudShellSession -Name e2e -ConnectingTimeout 120");

        var pid = Run("Invoke-Command -Session $s { $PID }");
        Assert.Equal(_server.LastProcessId, (int)pid.Single().BaseObject);

        var name = Run("$s.Name; $s.State; $s.ComputerName");
        Assert.Equal(["e2e", "Opened", "AzCloudShell"], name.Select(o => o.ToString()));

        var echo = Run("$x = 'héllo ✓ 世界'; Invoke-Command -Session $s { \"$using:x!\" }");
        Assert.Equal("héllo ✓ 世界!", echo.Single().ToString());

        var big = Run("Invoke-Command -Session $s { 1..20000 | ForEach-Object { 'line' + $_ } } | Measure-Object | ForEach-Object Count");
        Assert.Equal(20000, (int)big.Single().BaseObject);

        var payload = Run("$blob = 'x' * 300000; Invoke-Command -Session $s { $using:blob.Length }");
        Assert.Equal(300000, (int)payload.Single().BaseObject);

        _ps.Commands.Clear();
        _ps.AddScript("Invoke-Command -Session $s { Write-Error 'remote boom' }");
        _ps.Invoke();
        Assert.Contains(_ps.Streams.Error, e => e.ToString().Contains("remote boom"));

        // The session remains usable after a remote error.
        Assert.Equal(_server.LastProcessId, (int)Run("Invoke-Command -Session $s { $PID }").Single().BaseObject);

        Run("Remove-PSSession $s");
        AssertEventually(() => _server.StoppedServers == 1 && _server.ClosedConnections == 1, "Remove-PSSession did not close the terminal.");
    }

    [Fact]
    public void DisconnectPSSession_IsRejectedAndKeepsTheSessionUsable()
    {
        Run("$global:s = New-JAzCloudShellSession -ConnectingTimeout 120");

        // PowerShell only supports disconnecting WSMan sessions, so the session stays connected and usable.
        _ps.Commands.Clear();
        _ps.Streams.Error.Clear();
        _ps.AddScript("Disconnect-PSSession $s");
        _ps.Invoke();
        Assert.Contains(_ps.Streams.Error, e => e.ToString().Contains("not supported"));
        Assert.Equal(["Opened", "1"], Run("$s.State; Invoke-Command -Session $s { 1 }").Select(o => o.ToString()));
        Assert.Equal(0, _server.ClosedConnections);

        Run("Remove-PSSession $s");
        AssertEventually(() => _server.StoppedServers == 1 && _server.ClosedConnections == 1, "Remove-PSSession did not close the terminal.");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Contexts_SelectTokensAndProvisionEachSessionForArraysAndPipeline(bool pipeline)
    {
        SetUpContextAuthentication();
        Run(pipeline
            ? "$global:sessions = @($profiles[0], $profiles[1], $profiles[2], $profiles[0]) | New-JAzCloudShellSession"
            : "$global:sessions = New-JAzCloudShellSession -Context @($profiles[0], $profiles[1], $profiles[2], $profiles[0])");

        Assert.Equal(["4", "4", "alice", "bob", "alice", "alice"], Run("$sessions.Count; $tokenRequests.Count; $tokenRequests.Account").Select(o => o.ToString()));
        Assert.Equal(["tenant", "tenant", "other-tenant", "tenant"], Run("$tokenRequests.Tenant").Select(o => o.ToString()));
        Assert.Equal(4, (int)Run("@(Invoke-Command -Session $sessions { $PID } | Select-Object -Unique).Count").Single().BaseObject);
        Assert.Equal(["token-alice-tenant", "token-bob-tenant", "token-alice-other-tenant", "token-alice-tenant"], _terminalTokens);
        Assert.Equal(Enumerable.Repeat(new[] { "GET", "PUT", "POST" }, 4).SelectMany(methods => methods), _requestMethods);
        Run("$sessions | Remove-PSSession");
        AssertEventually(() => _server.StoppedServers == 4 && _server.ClosedConnections == 4, "Context sessions did not close their terminals.");
    }

    [Fact]
    public void ContextFailure_ReportsAnErrorAndContinuesWithTheNextProfile()
    {
        SetUpContextAuthentication();
        _ps.Commands.Clear();
        _ps.AddScript("$global:sessions = New-JAzCloudShellSession -Context @('not a profile', $profiles[0]) -ErrorAction Continue");
        _ps.Invoke();
        Assert.Single(_ps.Streams.Error);
        Assert.Equal(["1", "alice"], Run("@($sessions).Count; $tokenRequests.Account").Select(o => o.ToString()));
        Run("$sessions | Remove-PSSession");
    }

    [Fact]
    public void ContextTokenFailure_DoesNotFallBackToAnotherCredential()
    {
        SetUpContextAuthentication();
        Run("$global:failToken = $true");
        _ps.Commands.Clear();
        _ps.AddScript("New-JAzCloudShellSession -Context $profiles[0] -ErrorAction Continue");
        Assert.Empty(_ps.Invoke());
        Assert.Single(_ps.Streams.Error);
        Assert.Contains("context token failed", _ps.Streams.Error[0].ToString());
        Assert.Empty(_terminalTokens);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("expired")]
    public void ContextTokenRequest_RejectsInvalidNativeTokenResults(string mode)
    {
        SetUpContextAuthentication();
        Run($"$global:tokenMode = '{mode}'");
        _ps.Commands.Clear();
        _ps.AddScript("New-JAzCloudShellSession -Context $profiles[0] -ErrorAction Continue");
        Assert.Empty(_ps.Invoke());
        var error = Assert.Single(_ps.Streams.Error);
        Assert.Contains(mode == "invalid" ? "valid secure access token" : "expired access token", error.ToString());
        Assert.Empty(_terminalTokens);
    }

    [Fact]
    public void ContextTokenRequest_ObservesTheConnectionTimeout()
    {
        SetUpContextAuthentication();
        Run("$global:delayToken = $true");
        _ps.Commands.Clear();
        _ps.AddScript("New-JAzCloudShellSession -Context $profiles[0] -ConnectingTimeout 1 -ErrorAction Continue");
        var timer = Stopwatch.StartNew();
        Assert.Empty(_ps.Invoke());
        Assert.Single(_ps.Streams.Error);
        Assert.Contains("did not complete within 1 seconds", _ps.Streams.Error[0].ToString());
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "Token acquisition ignored the connection timeout.");
        Assert.Empty(_terminalTokens);
    }

    [Fact]
    public void AccessToken_AcceptsSecureStringWithRequiredAzAccountsModule()
    {
        SetUpTokenAuthentication();
        Run("""
            $token = ConvertTo-SecureString 'provided-raw-token' -AsPlainText -Force
            $global:sessions = @(
                New-JAzCloudShellSession -AccessToken $token
                New-JAzCloudShellSession -AccessToken $token
            )
            """);

        Assert.Single(Run("Get-Module Az.Accounts"));
        Assert.Equal(["provided-raw-token", "provided-raw-token"], _terminalTokens);
        Assert.Equal(2, _requestMethods.Count(m => m == "PUT"));
        Assert.Equal(["GET", "PUT", "POST", "GET", "PUT", "POST"], _requestMethods);
        Assert.Equal(["2"], Run("@(Invoke-Command -Session $sessions { $PID } | Select-Object -Unique).Count").Select(o => o.ToString()));
        Run("$sessions | Remove-PSSession");
        AssertEventually(() => _server.StoppedServers == 2 && _server.ClosedConnections == 2, "Supplied-token sessions did not close.");
    }

    [Fact]
    public void AccessToken_AcceptsSecureStringValuesFromAzTokenObjects()
    {
        SetUpSecureTokenObjects();
        Run("""
            $global:sessions = @(
                New-JAzCloudShellSession -AccessToken $tokens[0].Token
                New-JAzCloudShellSession -AccessToken $tokens[1].Token
                New-JAzCloudShellSession -AccessToken $tokens[0].Token
            )
            """);

        Assert.Equal(["provided-first-token", "provided-second-token", "provided-first-token"], _terminalTokens);
        Assert.Equal(Enumerable.Repeat(new[] { "GET", "PUT", "POST" }, 3).SelectMany(methods => methods), _requestMethods);
        Assert.Equal(["3"], Run("@(Invoke-Command -Session $sessions { $PID } | Select-Object -Unique).Count").Select(o => o.ToString()));
        Run("$sessions | Remove-PSSession");
        AssertEventually(() => _server.StoppedServers == 3 && _server.ClosedConnections == 3, "Supplied-token sessions did not close.");
    }

    [Fact]
    public void AccessToken_RejectsPSSecureAccessTokenObject()
    {
        SetUpSecureTokenObjects();
        _ps.Commands.Clear();
        _ps.AddScript("New-JAzCloudShellSession -AccessToken $tokens[0]");
        Assert.Empty(_ps.Invoke());
        Assert.Single(_ps.Streams.Error);
        Assert.Empty(_requestMethods);
    }

    [Theory]
    [InlineData("'provided-plaintext-token'")]
    [InlineData("[Security.SecureString]::new()")]
    [InlineData("ConvertTo-SecureString 'provided whitespace-token' -AsPlainText -Force")]
    public void AccessToken_RejectsInvalidInputWithoutDisclosingIt(string input)
    {
        SetUpTokenAuthentication();
        _ps.AddScript($"New-JAzCloudShellSession -AccessToken ({input})");
        Assert.Empty(_ps.Invoke());
        var error = Assert.Single(_ps.Streams.Error);
        Assert.Contains("AccessToken", error.ToString());
        Assert.DoesNotContain("provided", error.ToString());
        Assert.Empty(_requestMethods);
    }

    [Theory]
    [InlineData("-Context ([pscustomobject]@{})")]
    [InlineData("-TenantId 'tenant'")]
    [InlineData("-CredentialSource AzureCLI")]
    public void AccessToken_HasASeparateParameterSet(string otherParameter)
    {
        SetUpTokenAuthentication();
        _ps.AddScript($"New-JAzCloudShellSession -AccessToken ([Security.SecureString]::new()) {otherParameter}");
        Assert.Empty(_ps.Invoke());
        Assert.Contains("AmbiguousParameterSet", Assert.Single(_ps.Streams.Error).FullyQualifiedErrorId);
        Assert.Empty(_requestMethods);
    }

    private readonly List<string> _terminalTokens = [];
    private readonly List<string> _requestMethods = [];

    private void SetUpTokenAuthentication() =>
        NewJAzCloudShellSessionCommand.HttpClientFactoryOverride = () => new HttpClient(new ContextHttpHandler(_terminalTokens, _requestMethods));

    private void SetUpSecureTokenObjects()
    {
        Assert.SkipWhen((bool)Run("@(Get-Module -ListAvailable Az.Accounts).Count -eq 0").Single().BaseObject,
            "PSSecureAccessToken integration tests require Az.Accounts.");
        Run("""
            Import-Module Az.Accounts -ErrorAction Stop
            $global:tokens = @('provided-first-token', 'provided-second-token') | ForEach-Object {
                $token = [Microsoft.Azure.Commands.Profile.Models.PSSecureAccessToken]::new()
                $token.Token = ConvertTo-SecureString $_ -AsPlainText -Force
                $token.ExpiresOn = [DateTimeOffset]::UtcNow.AddHours(1)
                $token
            }
            """);
        SetUpTokenAuthentication();
    }

    private void SetUpContextAuthentication()
    {
        Assert.SkipWhen((bool)Run("@(Get-Module -ListAvailable Az.Accounts).Count -eq 0").Single().BaseObject,
            "Context integration tests require Az.Accounts.");
        Run("""
            Import-Module Az.Accounts -ErrorAction Stop
            $global:tokenRequests = [Collections.Generic.List[object]]::new()
            $global:failToken = $false
            $global:delayToken = $false
            $global:tokenMode = ''
            $global:profiles = @('alice', 'bob', 'alice') | ForEach-Object {
                $context = [Microsoft.Azure.Commands.Common.Authentication.Abstractions.AzureContext]::new()
                $context.Account = [Microsoft.Azure.Commands.Common.Authentication.Abstractions.AzureAccount]::new()
                $context.Account.Id = $_
                $context.Account.Type = 'User'
                $context.Tenant = [Microsoft.Azure.Commands.Common.Authentication.Abstractions.AzureTenant]::new()
                $context.Tenant.Id = 'tenant'
                $context.Environment = [Microsoft.Azure.Commands.Common.Authentication.Abstractions.AzureEnvironment]::PublicEnvironments['AzureCloud']
                $profile = [Microsoft.Azure.Commands.Common.Authentication.Models.AzureRmProfile]::new()
                $profile.DefaultContext = $context
                $profile
            }
            $profiles[2].DefaultContext.Tenant.Id = 'other-tenant'
            New-Module -Name Az.Accounts -ScriptBlock {
                function Get-AzAccessToken {
                    param($DefaultProfile, $ResourceTypeName, [switch]$AsSecureString)
                    if ($global:failToken) { throw 'context token failed' }
                    if ($global:delayToken) { Start-Sleep -Seconds 30 }
                    if ($global:tokenMode -eq 'invalid') {
                        return [pscustomobject]@{
                            Token = ConvertTo-SecureString 'not-a-native-token' -AsPlainText -Force
                            ExpiresOn = [DateTimeOffset]::UtcNow.AddHours(1)
                        }
                    }
                    if ($ResourceTypeName -ne 'Arm' -or -not $AsSecureString) { throw 'Incorrect token request' }
                    $global:tokenRequests.Add([pscustomobject]@{
                        Account = $DefaultProfile.DefaultContext.Account.Id
                        Tenant = $DefaultProfile.DefaultContext.Tenant.Id
                    })
                    $token = [Microsoft.Azure.Commands.Profile.Models.PSSecureAccessToken]::new()
                    $token.Token = ConvertTo-SecureString "token-$($DefaultProfile.DefaultContext.Account.Id)-$($DefaultProfile.DefaultContext.Tenant.Id)" -AsPlainText -Force
                    $token.ExpiresOn = [DateTimeOffset]::UtcNow.AddHours(1)
                    if ($global:tokenMode -eq 'expired') { $token.ExpiresOn = [DateTimeOffset]::UtcNow.AddMinutes(-1) }
                    $token
                }
                Export-ModuleMember -Function Get-AzAccessToken
            } | Import-Module -Force
            """);
        SetUpTokenAuthentication();
    }

    private sealed class ContextHttpHandler(List<string> terminalTokens, List<string> methods) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            methods.Add(request.Method.Method);
            if (request.Method == HttpMethod.Post)
            {
                terminalTokens.Add(request.Headers.Authorization!.Parameter!);
            }

            var json = request.Method.Method switch
            {
                "PUT" => """{"properties":{"uri":"https://ux.console.azure.com/c/test/"}}""",
                "POST" => """{"id":"terminal"}""",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static void AssertEventually(Func<bool> condition, string message)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), message);
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        _ps.Dispose();
        _server.Dispose();
        NewJAzCloudShellSessionCommand.WebSocketUriOverride = null;
        NewJAzCloudShellSessionCommand.HttpClientFactoryOverride = null;
    }
}
