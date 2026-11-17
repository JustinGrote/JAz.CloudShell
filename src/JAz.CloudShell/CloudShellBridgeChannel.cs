using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

namespace JAz.CloudShell;

/// <summary>
/// Full-duplex Cloud Shell terminal WebSocket. Before the bridge is ready it exposes the decoded terminal text
/// for bootstrap pattern matching; afterwards the terminal is a raw pipe to <c>pwsh -s</c>, so every line written is a
/// PSRP packet for the server's stdin and every line received is a PSRP packet from its stdout.
/// </summary>
internal sealed class CloudShellBridgeChannel : IDisposable
{
    private const int MaxMessageChars = 3072;
    private const int MaxBootstrapText = 1024 * 1024;
    private const int MaxDiagnosticLines = 20;
    private const string CursorPositionQuery = "\e[6n";
    private const string CursorPositionReport = "\e[1;1R";

    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _sync = new();
    private readonly StringBuilder _text = new();
    private readonly Decoder _wsDecoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly List<string> _completedLines = [];
    private readonly Queue<string> _diagnostics = new();
    private readonly BlockingCollection<string> _lines = new(boundedCapacity: 1024);
    private readonly TaskCompletionSource _serverExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _exitMarker;
    private volatile bool _bridged;
    private Exception? _failure;
    private Action<string>? _onLine;
    private Action<Exception>? _onError;
    private Task? _receiveTask;
    private Thread? _dispatchThread;
    private volatile bool _closing;
    private string? _nonce;
    private int _disposed;
    private string _queryTail = string.Empty;

    private CloudShellBridgeChannel(WebSocket socket) => _socket = socket;

    public DateTime LastReceivedUtc { get; private set; }
    public bool HasReceived { get; private set; }

    /// <summary>Number of cursor position queries received; PSReadLine sends one when it starts reading a prompt.</summary>
    public int CursorQueryCount { get; private set; }

    public bool IsBridged => _bridged;

    /// <summary>Line that starts forwarding terminal input to the server's stdin.</summary>
    internal static string StartLine(string nonce) => $"JZSTART{nonce}";

    /// <summary>Line that ends the server's stdin, which shuts the server down.</summary>
    internal static string StopLine(string nonce) => $"JZSTOP{nonce}";

    public static async Task<CloudShellBridgeChannel> ConnectAsync(Uri uri, CancellationToken ct)
    {
        // A freshly provisioned Cloud Shell can briefly reject the relay connection while its listener starts.
        for (var attempt = 1; ; attempt++)
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            try
            {
                await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
                return FromSocket(socket);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
            {
                socket.Dispose();
                if (attempt >= 5)
                {
                    throw new CloudShellException("WebSocket", $"Could not connect to the Cloud Shell terminal: {ex.Message}", inner: ex);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
        }
    }

    internal static CloudShellBridgeChannel FromSocket(WebSocket socket)
    {
        var channel = new CloudShellBridgeChannel(socket);
        channel._receiveTask = Task.Run(channel.ReceiveLoopAsync);
        return channel;
    }

    /// <summary>Types text into the remote terminal.</summary>
    public async Task SendTextAsync(string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfFailed();
            await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Returns the unconsumed bootstrap text, with ANSI escape sequences removed.</summary>
    public string PeekText()
    {
        lock (_sync)
        {
            return Regex.Replace(_text.ToString(), @"\e\[[0-9;?]*[A-Za-z]|\e[=>]", string.Empty);
        }
    }

    /// <summary>Waits until the bootstrap text matches, consuming text through the end of the match.</summary>
    public async Task<Match> WaitForAsync(Regex pattern, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        while (true)
        {
            Task signal;
            lock (_sync)
            {
                var match = pattern.Match(_text.ToString());
                if (match.Success)
                {
                    _text.Remove(0, match.Index + match.Length);
                    return match;
                }

                ThrowIfFailed();
                signal = _signal.Task;
            }

            try
            {
                await signal.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for the Cloud Shell terminal to respond.");
            }
        }
    }

    /// <summary>
    /// Switches from terminal text to PSRP lines once the server-mode launcher is running, then releases its input
    /// filter so later lines reach <c>pwsh -s</c>.
    /// </summary>
    public async Task EnterBridgeModeAsync(string nonce, CancellationToken ct)
    {
        List<string> ready;
        lock (_sync)
        {
            _nonce = nonce;
            _exitMarker = $"~JZC{nonce}~";
            _bridged = true;
            var pending = _text.ToString();
            _text.Clear();
            ProcessOutput(pending);
            ready = TakeCompletedLines();
        }

        foreach (var line in ready)
        {
            _lines.Add(line, ct);
        }

        // The leading newline terminates anything the terminal sent before; the filter discards it.
        await SendTextAsync($"\n{StartLine(nonce)}\n", ct).ConfigureAwait(false);
    }

    /// <summary>Attaches the PSRP transport. Lines are delivered on a dedicated thread in arrival order.</summary>
    public void Bind(Action<string> onLine, Action<Exception> onError)
    {
        if (Interlocked.CompareExchange(ref _onLine, onLine, null) is not null)
        {
            throw new InvalidOperationException("This Cloud Shell bridge is already bound to a session.");
        }

        _onError = onError;
        _dispatchThread = new Thread(DispatchLines) { IsBackground = true, Name = "JAz.CloudShell PSRP reader" };
        _dispatchThread.Start();
        Exception? failure;
        lock (_sync)
        {
            failure = _failure;
        }

        if (failure is not null && !_closing)
        {
            onError(failure);
        }
    }

    /// <summary>Sends PSRP text (complete lines) to the server's stdin. Blocks until written (backpressure).</summary>
    public void WriteData(string text)
    {
        if (!_bridged)
        {
            throw new InvalidOperationException("Bridge is not ready.");
        }

        for (var offset = 0; offset < text.Length; offset += MaxMessageChars)
        {
            SendTextAsync(text.Substring(offset, Math.Min(MaxMessageChars, text.Length - offset)), _cts.Token).GetAwaiter().GetResult();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var chars = new char[buffer.Length + 4];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Fail(new CloudShellException("WebSocket", $"The Cloud Shell terminal closed the connection ({_socket.CloseStatus} {_socket.CloseStatusDescription})."));
                    return;
                }

                var count = _wsDecoder.GetChars(buffer, 0, result.Count, chars, 0, flush: false);
                if (count == 0)
                {
                    continue;
                }

                var text = new string(chars, 0, count);
                if (!_bridged)
                {
                    var cursorQueries = CountCursorQueries(ref _queryTail, text);
                    CursorQueryCount += cursorQueries;
                    for (var q = 0; q < cursorQueries; q++)
                    {
                        // Behave like a terminal emulator: .NET/PSReadLine on Linux block reading stdin until the
                        // cursor position report arrives, consuming (and corrupting) anything typed meanwhile.
                        _ = Task.Run(SendCursorPositionReportAsync);
                    }
                }

                List<string> ready;
                lock (_sync)
                {
                    LastReceivedUtc = DateTime.UtcNow;
                    HasReceived = true;
                    if (!_bridged)
                    {
                        _text.Append(text);
                        if (_text.Length > MaxBootstrapText)
                        {
                            _text.Remove(0, _text.Length - (MaxBootstrapText / 2));
                        }
                    }
                    else
                    {
                        ProcessOutput(text);
                    }

                    ready = TakeCompletedLines();
                    Pulse();
                }

                // Add outside the lock: the bounded queue applies backpressure to the WebSocket reader.
                foreach (var line in ready)
                {
                    _lines.Add(line, _cts.Token);
                }
            }
        }
        catch (Exception ex) when (!_closing)
        {
            Fail(ex is CloudShellException ? ex : new CloudShellException("WebSocket", $"The Cloud Shell connection failed: {ex.Message}", inner: ex));
        }
        catch
        {
            // Closing by request.
        }
    }

    private async Task SendCursorPositionReportAsync()
    {
        try
        {
            await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                // Once server mode is running, terminal input is PSRP; a stray report would corrupt it.
                if (!_bridged && _failure is null)
                {
                    await _socket.SendAsync(Encoding.ASCII.GetBytes(CursorPositionReport), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch
        {
            // The receive loop reports connection failures.
        }
    }

    /// <summary>Counts cursor position queries (DSR 6), including one split across received messages.</summary>
    internal static int CountCursorQueries(ref string tail, string text)
    {
        var s = tail + text;
        int found = 0, pos = 0, i;
        while ((i = s.IndexOf(CursorPositionQuery, pos, StringComparison.Ordinal)) >= 0)
        {
            found++;
            pos = i + CursorPositionQuery.Length;
        }

        tail = s[Math.Max(pos, s.Length - (CursorPositionQuery.Length - 1))..];
        return found;
    }

    // Caller holds _sync.
    private void ProcessOutput(string text)
    {
        foreach (var c in text)
        {
            if (c != '\n')
            {
                _line.Append(c);
                continue;
            }

            if (_line.Length > 0 && _line[^1] == '\r')
            {
                _line.Length--;
            }

            var line = _line.ToString();
            _line.Clear();
            if (line.StartsWith('<'))
            {
                _completedLines.Add(line);
            }
            else if (line.Contains(_exitMarker!, StringComparison.Ordinal))
            {
                _serverExited.TrySetResult();
                if (!_closing)
                {
                    var detail = _diagnostics.Count > 0 ? $" Output: {string.Join(" ", _diagnostics)}" : string.Empty;
                    FailLocked(new CloudShellException("Bridge", $"PowerShell server mode (pwsh -s) in Cloud Shell exited.{detail}"));
                }
            }
            else if (line.Trim().Length > 0)
            {
                // Server stderr (e.g. a startup failure) shares the stream; keep it for the error message.
                _diagnostics.Enqueue(line.Trim());
                if (_diagnostics.Count > MaxDiagnosticLines)
                {
                    _diagnostics.Dequeue();
                }
            }
        }
    }

    // Caller holds _sync.
    private List<string> TakeCompletedLines()
    {
        if (_completedLines.Count == 0)
        {
            return [];
        }

        var lines = new List<string>(_completedLines);
        _completedLines.Clear();
        return lines;
    }

    private void DispatchLines()
    {
        try
        {
            foreach (var line in _lines.GetConsumingEnumerable(_cts.Token))
            {
                _onLine!(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (!_closing)
        {
            Fail(ex);
        }
    }

    private void Fail(Exception ex)
    {
        lock (_sync)
        {
            FailLocked(ex);
            Pulse();
        }
    }

    private void FailLocked(Exception ex)
    {
        if (_failure is not null)
        {
            return;
        }

        _failure = ex;
        Pulse();
        if (!_closing && _onError is { } onError)
        {
            ThreadPool.QueueUserWorkItem(_ => onError(ex));
        }
    }

    private void Pulse()
    {
        var old = _signal;
        _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }

    private void ThrowIfFailed()
    {
        if (_failure is { } failure)
        {
            throw failure is CloudShellException ? failure : new CloudShellException("WebSocket", failure.Message, inner: failure);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _closing = true;
        try
        {
            if (_nonce is { } nonce && _failure is null && !_serverExited.Task.IsCompleted && _socket.State == WebSocketState.Open)
            {
                // End the server's stdin so it exits and the terminal returns to its prompt.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                SendTextAsync($"\n{StopLine(nonce)}\n", timeout.Token).GetAwaiter().GetResult();
                _serverExited.Task.Wait(TimeSpan.FromSeconds(3));
            }

            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session closed", timeout.Token).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Best-effort close.
        }

        _cts.Cancel();
        _socket.Abort();
        _socket.Dispose();
        _lines.CompleteAdding();
    }
}
