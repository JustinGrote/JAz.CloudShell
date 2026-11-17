using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;

namespace JAz.CloudShell;

public enum AzureCredentialSource
{
    Environment,
    VSCode,
    AzureCLI,
    WAM,
}

internal static class DefaultAuthentication
{
    private const string BrokerClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    internal static ValueTask<AccessToken> GetTokenAsync(
        string? tenantId, AzureCredentialSource? source, TokenRequestContext request, Action<string> log, CancellationToken ct) =>
        GetTokenAsync(CreateDefaultCredentials(tenantId, source), request, log, ct);

    internal static async ValueTask<AccessToken> GetTokenAsync(
        IEnumerable<(string Name, TokenCredential Credential)> credentials,
        TokenRequestContext request,
        Action<string> log,
        CancellationToken ct)
    {
        var credentialList = credentials.ToArray();
        var unavailable = new List<string>();
        CredentialUnavailableException? lastUnavailable = null;
        for (var i = 0; i < credentialList.Length; i++)
        {
            var (name, credential) = credentialList[i];
            ct.ThrowIfCancellationRequested();
            log($"Checking {name} for an Azure Resource Manager token.");
            try
            {
                var token = await credential.GetTokenAsync(request, ct).ConfigureAwait(false);
                log($"Acquired an Azure Resource Manager token with {name}.");
                return token;
            }
            catch (CredentialUnavailableException ex)
            {
                ct.ThrowIfCancellationRequested();
                unavailable.Add(name);
                lastUnavailable = ex;
                log(i + 1 < credentialList.Length
                    ? $"{name} is unavailable; trying the next credential."
                    : $"{name} is unavailable.");
            }
        }

        throw new CredentialUnavailableException(
            $"No Azure token was available from the configured credentials: {string.Join(", ", unavailable)}.",
            lastUnavailable);
    }

    internal static (string Name, TokenCredential Credential)[] CreateDefaultCredentials(
        string? tenantId, AzureCredentialSource? source = null)
    {
        List<(string Name, TokenCredential Credential)> credentials = [];

        if (source is null or AzureCredentialSource.Environment)
        {
            credentials.Add(("EnvironmentCredential", new EnvironmentCredential()));
        }

        if (source is null or AzureCredentialSource.VSCode)
        {
            credentials.Add(("VisualStudioCodeCredential", new VisualStudioCodeCredential(new VisualStudioCodeCredentialOptions { TenantId = tenantId })));
        }

        if (source is null or AzureCredentialSource.AzureCLI)
        {
            credentials.Add(("AzureCliCredential", new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId })));
        }

        if (source == AzureCredentialSource.WAM || (source is null && OperatingSystem.IsWindows()))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new CredentialUnavailableException("The WAM credential source is only available on Windows.");
            }

            credentials.Add(("BrokerCredential", new InteractiveBrowserCredential(new InteractiveBrowserCredentialBrokerOptions(IntPtr.Zero)
            {
                ClientId = BrokerClientId,
                TenantId = tenantId,
                UseDefaultBrokerAccount = true,
                DisableAutomaticAuthentication = true,
            })));
        }

        return credentials.ToArray();
    }
}
