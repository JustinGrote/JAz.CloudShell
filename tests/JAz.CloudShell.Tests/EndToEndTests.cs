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
    /// Emulates the Cloud Shell terminal: an interactive pwsh prompt that echoes input until the launch line arrives,
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
            // Look like PSReadLine at an idle prompt, which queries the cursor position before reading input.
            await SendAsync("PS /home/test> \e[6n"u8.ToArray());

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
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (nonce is null)
                {
                    // Interactive prompt: PSReadLine consumes cursor reports and echoes typed text.
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
                        break;
                    }

                    await stdin.WriteAsync(l + "\n");
                }

                if (stopped)
                {
                    break;
                }

                await stdin.FlushAsync();
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

public sealed class EndToEndTests : IDisposable
{
    private readonly FakeTerminalServer _server = new();
    private readonly PowerShell _ps;

    public EndToEndTests()
    {
        NewAzCloudShellSessionCommand.WebSocketUriOverride = _server.Uri;
        var iss = InitialSessionState.CreateDefault2();
        iss.Commands.Add(new SessionStateCmdletEntry("New-AzCloudShellSession", typeof(NewAzCloudShellSessionCommand), null));
        _ps = PowerShell.Create(iss);
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
        Run("$global:s = New-AzCloudShellSession -Name e2e -ConnectingTimeout 120");

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
    }

    public void Dispose()
    {
        _ps.Dispose();
        _server.Dispose();
        NewAzCloudShellSessionCommand.WebSocketUriOverride = null;
    }
}
