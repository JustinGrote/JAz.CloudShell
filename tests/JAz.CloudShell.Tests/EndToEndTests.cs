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
/// stdin/stdout are relayed over the socket. This exercises the real bootstrap, remote helper, named pipe, and PSRP.
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

    private async Task ServeAsync(WebSocket ws)
    {
        var psi = new ProcessStartInfo("pwsh", "-NoLogo -NoProfile -NonInteractive -Command -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        lock (_processes)
        {
            _processes.Add(process);
        }

        LastProcessId = process.Id;
        process.ErrorDataReceived += (_, e) =>
        {
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
            }
        };
        process.BeginErrorReadLine();

        var sendLock = new SemaphoreSlim(1, 1);
        var output = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer)) > 0)
            {
                await sendLock.WaitAsync();
                try
                {
                    await ws.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                finally
                {
                    sendLock.Release();
                }
            }
        });

        try
        {
            // Look like PSReadLine at an idle prompt, which queries the cursor position before reading input.
            await sendLock.WaitAsync();
            try
            {
                await ws.SendAsync("PS /home/test> \e[6n"u8.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            finally
            {
                sendLock.Release();
            }

            var buffer = new byte[16384];
            var stdin = process.StandardInput.BaseStream;
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                // PSReadLine consumes cursor position reports; the redirected pwsh would try to execute them.
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count).Replace("\e[1;1R", string.Empty, StringComparison.Ordinal);

                // Emulate a cooked-mode PTY (icrnl): Enter (CR) reaches the shell as LF.
                await stdin.WriteAsync(Encoding.UTF8.GetBytes(text.Replace('\r', '\n')));
                await stdin.FlushAsync();
            }
        }
        catch
        {
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            ws.Dispose();
        }

        await Task.WhenAny(output, Task.Delay(2000));
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
