using System.Collections.Concurrent;
using Azure.Core;

namespace JAz.CloudShell;

/// <summary>
/// Remembers the access token and Cloud Shell console per tenant for this process, so later sessions only create a new
/// terminal in the running console instead of authenticating and provisioning again.
/// </summary>
internal static class CloudShellConsoleCache
{
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(AccessToken Token, Uri ConsoleUri);

    /// <summary>Creates a terminal and returns its WebSocket URI, and whether a cached console was reused.</summary>
    public static async Task<(Uri WebSocketUri, bool Reused)> CreateTerminalAsync(
        string key,
        Func<CancellationToken, ValueTask<AccessToken>> getToken,
        HttpClient http,
        int columns,
        int rows,
        Action<string> log,
        CancellationToken ct)
    {
        if (Entries.TryGetValue(key, out var cached) && cached.Token.ExpiresOn - DateTimeOffset.UtcNow > TokenRefreshMargin)
        {
            log("Reusing the existing Cloud Shell console.");
            try
            {
                var uri = await new CloudShellClient(http, cached.Token.Token, log).CreateTerminalAsync(cached.ConsoleUri, columns, rows, ct).ConfigureAwait(false);
                return (uri, true);
            }
            catch (CloudShellException ex)
            {
                // The console may have timed out or been restarted elsewhere.
                log($"Could not reuse the Cloud Shell console ({ex.Message}); requesting it again.");
                Invalidate(key);
            }
        }

        log("Acquiring an Azure Resource Manager token with DefaultAzureCredential.");
        var token = await getToken(ct).ConfigureAwait(false);
        var client = new CloudShellClient(http, token.Token, log);
        var consoleUri = await client.ProvisionConsoleAsync(ct).ConfigureAwait(false);
        var webSocketUri = await client.CreateTerminalAsync(consoleUri, columns, rows, ct).ConfigureAwait(false);
        Entries[key] = new Entry(token, consoleUri);
        return (webSocketUri, false);
    }

    public static void Invalidate(string key) => Entries.TryRemove(key, out _);

    internal static void Clear() => Entries.Clear();
}
