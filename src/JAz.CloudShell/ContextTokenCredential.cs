using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;

namespace JAz.CloudShell;

internal static class ContextTokenCredential
{
    private const string ClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    internal static AuthenticationRecord CreateRecord(string username, string homeAccountId, string tenantId)
    {
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            username,
            homeAccountId,
            tenantId,
            authority = "login.microsoftonline.com",
            clientId = ClientId,
            version = "1.0",
        }));
        return AuthenticationRecord.Deserialize(stream);
    }

    internal static ValueTask<AccessToken> GetTokenAsync(
        AccessToken contextToken, string tenantId, AuthenticationRecord? record, Action<string> log, CancellationToken ct)
    {
        TokenCredential? broker = OperatingSystem.IsWindows() && record is not null
            ? new InteractiveBrowserCredential(new InteractiveBrowserCredentialBrokerOptions(IntPtr.Zero)
            {
                ClientId = ClientId,
                TenantId = tenantId,
                AuthenticationRecord = record,
                DisableAutomaticAuthentication = true,
                UseDefaultBrokerAccount = false,
            })
            : null;
        return GetTokenAsync(contextToken, tenantId, broker,
            new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId }), log, ct);
    }

    internal static async ValueTask<AccessToken> GetTokenAsync(
        AccessToken contextToken, string tenantId, TokenCredential? broker, TokenCredential cli,
        Action<string> log, CancellationToken ct)
    {
        var expected = ReadIdentity(contextToken);
        if (!Guid.TryParse(tenantId, out var tenant) || expected.Tenant != tenant)
        {
            throw new AuthenticationFailedException("The Az.Accounts token does not match the supplied context's tenant.");
        }

        var request = new TokenRequestContext([CloudShellClient.ArmScope], tenantId: tenantId);
        ct.ThrowIfCancellationRequested();
        AccessToken token;
        if (broker is not null)
        {
            log("Checking WAM silently for the supplied context's account and tenant.");
            try
            {
                token = await broker.GetTokenAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is AuthenticationFailedException or CredentialUnavailableException)
            {
                log($"Account-specific WAM authentication was unavailable ({ex.GetType().Name}); checking Azure CLI for the same identity.");
                token = await cli.GetTokenAsync(request, ct).ConfigureAwait(false);
            }
        }
        else
        {
            log("Checking Azure CLI for a token matching the supplied context's account and tenant.");
            token = await cli.GetTokenAsync(request, ct).ConfigureAwait(false);
        }

        if (token.ExpiresOn <= DateTimeOffset.UtcNow || ReadIdentity(token) != expected)
        {
            throw new AuthenticationFailedException(
                "The Cloud Shell credential did not match the supplied context's account and tenant, or has expired. " +
                "Sign in to WAM or Azure CLI as that account in that tenant and retry. No session was created.");
        }

        log("Using a Cloud Shell-compatible token verified against the supplied context's account and tenant.");
        return token;
    }

    private static (Guid Tenant, Guid Account) ReadIdentity(AccessToken token)
    {
        var parts = token.Token.Split('.');
        if (parts.Length != 3)
        {
            throw new AuthenticationFailedException("Cannot verify the account and tenant of the access token.");
        }

        try
        {
            using var claims = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(parts[1]));
            if (claims.RootElement.TryGetProperty("tid", out var tid) && tid.TryGetGuid(out var tenant) &&
                claims.RootElement.TryGetProperty("oid", out var oid) && oid.TryGetGuid(out var account))
            {
                return (tenant, account);
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            throw new AuthenticationFailedException("Cannot read the account and tenant of the access token.", ex);
        }

        throw new AuthenticationFailedException("The access token has no verifiable account and tenant; refusing to switch identities.");
    }
}
