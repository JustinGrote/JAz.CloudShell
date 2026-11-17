using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace JAz.CloudShell;

/// <summary>
/// Turns the interactive Cloud Shell terminal into a PowerShell remoting stream: it types one command that starts
/// <c>pwsh -s</c> (server mode) so the terminal carries PSRP to the server's stdin and from its stdout.
/// </summary>
internal static class Bootstrapper
{
    public static async Task RunAsync(CloudShellBridgeChannel channel, Action<string> log, CancellationToken ct)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

        log("Waiting for the Cloud Shell PowerShell prompt.");
        await WaitForPromptAsync(channel, ct).ConfigureAwait(false);

        // The marker is assembled by printf so the terminal's echo of the command never matches it.
        var ready = new Regex($@"~JZR{nonce}~");
        var command = BuildLaunchCommand(nonce);
        for (var attempt = 1; ; attempt++)
        {
            log("Starting PowerShell in server mode (pwsh -s).");
            await channel.SendTextAsync(command, ct).ConfigureAwait(false);
            try
            {
                await channel.WaitForAsync(ready, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                break;
            }
            catch (TimeoutException ex) when (attempt >= 3)
            {
                throw new CloudShellException("Bootstrap", "Timed out waiting for PowerShell server mode to start in Cloud Shell.", inner: ex);
            }
            catch (TimeoutException)
            {
                // A line typed before PSReadLine reads input is lost. If an earlier attempt did start, this retry is
                // discarded by the server's input filter, which drops everything before the start line.
            }
        }

        await channel.EnterBridgeModeAsync(nonce, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the line typed at the Cloud Shell prompt. The terminal is switched to raw mode without echo, so PSRP
    /// bytes pass through unchanged. Pipes keep .NET in the server from treating stdin/stdout as a terminal. Input
    /// lines are discarded until the start line, so leftover terminal replies can't reach the server, and the stop
    /// line ends the server's stdin. After the server exits the terminal is restored and an exit marker is printed.
    /// </summary>
    internal static string BuildLaunchCommand(string nonce)
    {
        var script =
            "stty raw -echo; " +
            $"printf \"~JZ%s%s~\\n\" R {nonce}; " +
            $"{{ while IFS= read -r l; do case \"$l\" in *{CloudShellBridgeChannel.StartLine(nonce)}*) break;; esac; done; " +
            $"while IFS= read -r l; do [ \"$l\" = \"{CloudShellBridgeChannel.StopLine(nonce)}\" ] && break; printf \"%s\\n\" \"$l\"; done; }} " +
            "| \"$JZPS\" -NoLogo -NoProfile -s 2>&1 | cat; " +
            "stty sane; " +
            $"printf \"~JZ%s%s~\\n\" C {nonce}";

        return "try { Set-PSReadLineOption -HistorySaveStyle SaveNothing -PredictionSource None } catch { }; " +
            "$env:JZPS = [Environment]::ProcessPath; " +
            $"bash -c '{script}'; " +
            "Remove-Item env:JZPS\r";
    }

    private static async Task WaitForPromptAsync(CloudShellBridgeChannel channel, CancellationToken ct)
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
