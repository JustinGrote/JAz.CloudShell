using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

namespace JAz.CloudShell;

/// <summary>Encodes and incrementally parses nonce-scoped bridge frames: <c>~JZ&lt;nonce&gt;|&lt;kind&gt;|&lt;base64&gt;~</c>.</summary>
internal sealed class FrameParser
{
    internal const int MaxFrameLength = 64 * 1024;
    private readonly string _prefix;
    private readonly StringBuilder _buffer = new();

    public FrameParser(string nonce) => _prefix = $"~JZ{nonce}|";

    public static string Encode(string nonce, char kind, ReadOnlySpan<byte> payload) =>
        $"~JZ{nonce}|{kind}|{Convert.ToBase64String(payload)}~\n";

    /// <summary>Appends terminal text and returns every complete frame. Text outside frames is discarded.</summary>
    public IEnumerable<(char Kind, byte[] Payload)> Feed(string text)
    {
        _buffer.Append(text);
        var results = new List<(char, byte[])>();
        while (true)
        {
            var s = _buffer.ToString();
            var start = s.IndexOf(_prefix, StringComparison.Ordinal);
            if (start < 0)
            {
                var keep = Math.Min(_prefix.Length - 1, s.Length);
                _buffer.Clear().Append(s, s.Length - keep, keep);
                break;
            }

            var bodyStart = start + _prefix.Length;
            var end = s.IndexOf('~', bodyStart);
            if (end < 0)
            {
                _buffer.Clear().Append(s, start, s.Length - start);
                if (_buffer.Length > MaxFrameLength)
                {
                    throw new InvalidDataException("A bridge frame exceeded the maximum allowed length.");
                }

                break;
            }

            if (end - bodyStart < 2 || s[bodyStart + 1] != '|')
            {
                throw new InvalidDataException("Malformed bridge frame received from Cloud Shell.");
            }

            var kind = s[bodyStart];
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(s.Substring(bodyStart + 2, end - bodyStart - 2));
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("Corrupt bridge frame payload received from Cloud Shell.", ex);
            }

            results.Add((kind, payload));
            _buffer.Clear().Append(s, end + 1, s.Length - end - 1);
        }

        return results;
    }
}

/// <summary>
/// Full-duplex Cloud Shell terminal WebSocket. Before the bridge is ready it exposes the decoded terminal text
/// for bootstrap pattern matching; afterwards it carries framed PSRP bytes to and from the remote helper.
/// </summary>
internal sealed class CloudShellBridgeChannel : IDisposable
{
    private const int MaxFramePayload = 768;
    private const int FramesPerMessage = 4;
    private const int MaxBootstrapText = 1024 * 1024;
    private const string CursorPositionQuery = "\e[6n";
    private const string CursorPositionReport = "\e[1;1R";

    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _sync = new();
    private readonly StringBuilder _text = new();
    private readonly Decoder _wsDecoder = new UTF8Encoding(false).GetDecoder();
    private readonly Decoder _psrpDecoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _psrpLine = new();
    private readonly List<string> _completedLines = [];
    private readonly BlockingCollection<string> _lines = new(boundedCapacity: 1024);
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FrameParser? _frames;
    private string? _nonce;
    private Exception? _failure;
    private Action<string>? _onLine;
    private Action<Exception>? _onError;
    private Task? _receiveTask;
    private Thread? _dispatchThread;
    private volatile bool _closing;
    private volatile bool _remoteClosed;
    private int _disposed;
    private string _queryTail = string.Empty;

    private CloudShellBridgeChannel(WebSocket socket) => _socket = socket;

    public DateTime LastReceivedUtc { get; private set; }
    public bool HasReceived { get; private set; }

    /// <summary>Number of cursor position queries received; PSReadLine sends one when it starts reading a prompt.</summary>
    public int CursorQueryCount { get; private set; }
    public bool IsBridged => _frames is not null;

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

    /// <summary>Switches from terminal text to framed bridge traffic. Text already received is parsed for frames.</summary>
    public void EnterBridgeMode(string nonce)
    {
        List<string> ready;
        lock (_sync)
        {
            _nonce = nonce;
            _frames = new FrameParser(nonce);
            var pending = _text.ToString();
            _text.Clear();
            ProcessFrames(pending);
            ready = TakeCompletedLines();
        }

        foreach (var line in ready)
        {
            _lines.Add(line);
        }
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

    /// <summary>Sends PSRP text (complete lines) to the remote named pipe. Blocks until written (backpressure).</summary>
    public void WriteData(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var nonce = _nonce ?? throw new InvalidOperationException("Bridge is not ready.");
        var message = new StringBuilder();
        var frames = 0;
        for (var offset = 0; offset < bytes.Length; offset += MaxFramePayload)
        {
            message.Append(FrameParser.Encode(nonce, 'd', bytes.AsSpan(offset, Math.Min(MaxFramePayload, bytes.Length - offset))));
            if (++frames == FramesPerMessage)
            {
                SendTextAsync(message.ToString(), _cts.Token).GetAwaiter().GetResult();
                message.Clear();
                frames = 0;
            }
        }

        if (message.Length > 0)
        {
            SendTextAsync(message.ToString(), _cts.Token).GetAwaiter().GetResult();
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

                var cursorQueries = CountCursorQueries(ref _queryTail, text);
                CursorQueryCount += cursorQueries;
                for (var q = 0; q < cursorQueries; q++)
                {
                    // Behave like a terminal emulator: .NET/PSReadLine on Linux block reading stdin until the
                    // cursor position report arrives, consuming (and corrupting) anything typed meanwhile.
                    _ = Task.Run(() => SendTextAsync(CursorPositionReport, _cts.Token)).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }

                List<string> ready;
                lock (_sync)
                {
                    LastReceivedUtc = DateTime.UtcNow;
                    HasReceived = true;
                    if (_frames is null)
                    {
                        _text.Append(text);
                        if (_text.Length > MaxBootstrapText)
                        {
                            _text.Remove(0, _text.Length - (MaxBootstrapText / 2));
                        }
                    }
                    else
                    {
                        ProcessFrames(text);
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
    private void ProcessFrames(string text)
    {
        foreach (var (kind, payload) in _frames!.Feed(text))
        {
            switch (kind)
            {
                case 'R':
                    break;
                case 'D':
                    AppendPsrp(payload);
                    break;
                case 'E':
                    FailLocked(new CloudShellException("Bridge", $"The Cloud Shell bridge helper reported an error: {Encoding.UTF8.GetString(payload)}"));
                    break;
                case 'C':
                    // The remote pipe also closes after a normal PSRP close handshake, which can race local cleanup.
                    // Only report it as a failure if the session is not being closed locally shortly afterwards.
                    _remoteClosed = true;
                    _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
                    {
                        if (!_closing)
                        {
                            Fail(new CloudShellException("Bridge", "The Cloud Shell PowerShell named pipe connection was closed."));
                        }
                    }, TaskScheduler.Default);
                    break;
                default:
                    // Lowercase kinds are local->remote frames (e.g. terminal echo); ignore.
                    break;
            }
        }
    }

    private void AppendPsrp(byte[] payload)
    {
        var chars = new char[_psrpDecoder.GetCharCount(payload, 0, payload.Length, flush: false)];
        _psrpDecoder.GetChars(payload, 0, payload.Length, chars, 0, flush: false);
        foreach (var c in chars)
        {
            if (c == '\n')
            {
                if (_psrpLine.Length > 0 && _psrpLine[^1] == '\r')
                {
                    _psrpLine.Length--;
                }

                _completedLines.Add(_psrpLine.ToString());
                _psrpLine.Clear();
            }
            else
            {
                _psrpLine.Append(c);
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
            if (_nonce is { } nonce && _failure is null && !_remoteClosed && _socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                SendTextAsync(FrameParser.Encode(nonce, 'c', ReadOnlySpan<byte>.Empty), timeout.Token).GetAwaiter().GetResult();
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
