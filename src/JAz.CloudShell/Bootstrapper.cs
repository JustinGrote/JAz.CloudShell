using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JAz.CloudShell;

internal sealed record BootstrapResult(int ProcessId, Version PSVersion, Version RuntimeVersion, string Nonce);

/// <summary>
/// Drives the interactive Cloud Shell PowerShell prompt: discovers <c>$PID</c>, uploads the bridge helper in
/// acknowledged chunks, and launches it so the terminal becomes a framed pipe to that process's remoting listener.
/// </summary>
internal static class Bootstrapper
{
    internal static readonly Version MinimumPSVersion = new(7, 6);
    internal static readonly Version MinimumRuntimeVersion = new(10, 0);
    private const int ChunkSize = 900;

    public static async Task<BootstrapResult> RunAsync(CloudShellBridgeChannel channel, Action<string> log, CancellationToken ct)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

        log("Waiting for the Cloud Shell PowerShell prompt.");
        await WaitForQuietAsync(channel, ct).ConfigureAwait(false);

        // The marker is split across string literals so the terminal's echo of the command never matches.
        var probe = $"try {{ Set-PSReadLineOption -HistorySaveStyle SaveNothing -PredictionSource None }} catch {{ }}; '~JZP' + '{nonce}|' + $PID + '|' + $PSVersionTable.PSVersion + '|' + [Environment]::Version + '~'\r";
        var probePattern = new Regex($@"~JZP{nonce}\|(\d+)\|([0-9][^|~\s]*)\|([0-9][^|~\s]*)~");
        Match? match = null;
        for (var attempt = 1; match is null; attempt++)
        {
            log("Discovering the Cloud Shell PowerShell process ($PID).");
            await channel.SendTextAsync(probe, ct).ConfigureAwait(false);
            try
            {
                match = await channel.WaitForAsync(probePattern, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            }
            catch (TimeoutException) when (attempt < 3)
            {
            }
        }

        var result = ParseProbe(match, nonce);
        log($"Cloud Shell PowerShell process {result.ProcessId} (PowerShell {result.PSVersion}, .NET {result.RuntimeVersion}).");
        ValidateRemoteVersion(result);

        var encoded = EncodeHelper(nonce, result.ProcessId);
        log($"Uploading the bridge helper ({encoded.Length} characters).");
        for (var i = 0; i * ChunkSize < encoded.Length; i++)
        {
            var chunk = encoded.Substring(i * ChunkSize, Math.Min(ChunkSize, encoded.Length - (i * ChunkSize)));
            var op = i == 0 ? "=" : "+=";
            await channel.SendTextAsync($"$jz{op}'{chunk}'; '~JZA' + '{nonce}|{i}~'\r", ct).ConfigureAwait(false);
            await channel.WaitForAsync(new Regex($@"~JZA{nonce}\|{i}~"), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(encoded)));
        await channel.SendTextAsync($"'~JZV' + '{nonce}|' + $jz.Length + '|' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($jz))) + '~'\r", ct).ConfigureAwait(false);
        var verify = await channel.WaitForAsync(new Regex($@"~JZV{nonce}\|(\d+)\|([0-9A-F]+)~"), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (verify.Groups[1].Value != encoded.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) || verify.Groups[2].Value != hash)
        {
            throw new CloudShellException("Bootstrap", "The bridge helper was corrupted while being uploaded to Cloud Shell.");
        }

        log("Starting the bridge helper and connecting to the PowerShell named pipe.");
        await channel.SendTextAsync("& ([Environment]::ProcessPath) -NoLogo -NoProfile -NonInteractive -EncodedCommand $jz; Remove-Variable jz; if (-not $IsWindows) { stty sane 2>$null }\r", ct).ConfigureAwait(false);
        Match ready;
        try
        {
            ready = await channel.WaitForAsync(new Regex($@"~JZ{nonce}\|([RE])\|([A-Za-z0-9+/=]*)~"), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new CloudShellException("Bootstrap", "Timed out waiting for the Cloud Shell bridge helper to connect to the PowerShell named pipe.", inner: ex);
        }

        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(ready.Groups[2].Value));
        if (ready.Groups[1].Value == "E")
        {
            throw new CloudShellException("Bridge", $"The Cloud Shell bridge helper failed: {payload}");
        }

        if (payload != "1")
        {
            throw new CloudShellException("Bridge", $"Unsupported bridge helper protocol version '{payload}'.");
        }

        channel.EnterBridgeMode(nonce);
        return result;
    }

    internal static BootstrapResult ParseProbe(Match match, string nonce)
    {
        var pid = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return new BootstrapResult(pid, ParseVersion(match.Groups[2].Value), ParseVersion(match.Groups[3].Value), nonce);
    }

    internal static void ValidateRemoteVersion(BootstrapResult result)
    {
        if (result.PSVersion < MinimumPSVersion || result.RuntimeVersion < MinimumRuntimeVersion)
        {
            throw new CloudShellException(
                "Bootstrap",
                $"Cloud Shell is running PowerShell {result.PSVersion} on .NET {result.RuntimeVersion}; JAz.CloudShell requires PowerShell {MinimumPSVersion}+ on .NET {MinimumRuntimeVersion}+.");
        }
    }

    private static Version ParseVersion(string text)
    {
        var core = text.Split('-', '+')[0];
        return Version.TryParse(core, out var v) ? v : throw new CloudShellException("Bootstrap", $"Could not parse remote version '{text}'.");
    }

    internal static string EncodeHelper(string nonce, int processId)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("JAz.CloudShell.RemoteBridge.ps1")
            ?? throw new InvalidOperationException("Embedded bridge helper is missing.");
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd()
            .Replace("__NONCE__", nonce, StringComparison.Ordinal)
            .Replace("__PID__", processId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !l.TrimStart().StartsWith('#') && l.Trim().Length > 0);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(string.Join('\n', lines)));
    }

    private static async Task WaitForQuietAsync(CloudShellBridgeChannel channel, CancellationToken ct)
    {
        // Cloud Shell's pwsh can take most of a minute to start; text typed before it reads input is only echoed by
        // the TTY and then lost. PSReadLine querying the cursor position (or a visible prompt) means it is reading.
        var prompt = new Regex(@"PS [^\r\n]*> ?$");
        var start = DateTime.UtcNow;
        DateTime? readyAt = null;
        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(150))
        {
            if (readyAt is null && (channel.CursorQueryCount > 0 || prompt.IsMatch(channel.PeekText())))
            {
                readyAt = DateTime.UtcNow;
            }

            if (readyAt is not null && (DateTime.UtcNow - channel.LastReceivedUtc > TimeSpan.FromMilliseconds(750) || DateTime.UtcNow - readyAt > TimeSpan.FromSeconds(3)))
            {
                return;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }
}
