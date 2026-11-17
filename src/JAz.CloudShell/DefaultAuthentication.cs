using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;

namespace JAz.CloudShell;

internal static class DefaultAuthentication
{
    internal static ValueTask<AccessToken> GetTokenAsync(string? tenantId, bool noWam, Action<string> log, CancellationToken ct)
    {
        TokenCredential? broker = OperatingSystem.IsWindows() && !noWam
            ? new InteractiveBrowserCredential(new InteractiveBrowserCredentialBrokerOptions(IntPtr.Zero)
            {
                // Azure CLI's public client is supported by both WAM and the Cloud Shell service.
                ClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46",
                TenantId = tenantId,
                UseDefaultBrokerAccount = true,
                DisableAutomaticAuthentication = true,
            })
            : null;
        return GetTokenAsync(broker, () => new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = tenantId,
            ExcludeBrokerCredential = true,
            ExcludeInteractiveBrowserCredential = true,
        }), new TokenRequestContext([CloudShellClient.ArmScope], tenantId: tenantId), log, ct);
    }

    internal static async ValueTask<AccessToken> GetTokenAsync(
        TokenCredential? broker,
        Func<TokenCredential> defaultCredential,
        TokenRequestContext request,
        Action<string> log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (broker is not null)
        {
            log("Checking Windows Web Account Manager (WAM) for a token without prompting.");
            try
            {
                var token = await broker.GetTokenAsync(request, ct).ConfigureAwait(false);
                log("Using a token from Windows Web Account Manager (WAM).");
                return token;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is AuthenticationFailedException or CredentialUnavailableException)
            {
                log($"Silent WAM authentication was unavailable ({ex.GetType().Name}); trying DefaultAzureCredential.");
            }
        }

        ct.ThrowIfCancellationRequested();
        return await defaultCredential().GetTokenAsync(request, ct).ConfigureAwait(false);
    }
}
