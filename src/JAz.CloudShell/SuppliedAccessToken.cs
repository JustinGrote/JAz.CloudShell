using System.Management.Automation;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Azure.Identity;

namespace JAz.CloudShell;

internal static class SuppliedAccessToken
{
    internal static (string CacheKey, AccessToken Token, bool CacheToken) Read(PSObject input)
    {
        SecureString secureToken;
        var expiresOn = DateTimeOffset.MinValue;
        var cacheToken = true;
        if (input.BaseObject is SecureString value)
        {
            secureToken = value;
            cacheToken = false;
        }
        else
        {
            var type = new PSTypeName("Microsoft.Azure.Commands.Profile.Models.PSSecureAccessToken").Type;
            if (type is null || !type.IsInstanceOfType(input.BaseObject))
            {
                throw new ArgumentException("AccessToken must be a SecureString or an Az.Accounts PSSecureAccessToken.", "AccessToken");
            }

            var tokenValue = input.Properties["Token"]?.Value;
            var expiryValue = input.Properties["ExpiresOn"]?.Value;
            if (tokenValue is null || PSObject.AsPSObject(tokenValue).BaseObject is not SecureString token ||
                expiryValue is null || PSObject.AsPSObject(expiryValue).BaseObject is not DateTimeOffset expiry)
            {
                throw new ArgumentException("PSSecureAccessToken must contain a SecureString Token and an ExpiresOn value.", "AccessToken");
            }

            secureToken = token;
            expiresOn = expiry;
            if (expiresOn <= DateTimeOffset.UtcNow)
            {
                throw new AuthenticationFailedException("The supplied access token has expired. Supply a new token and retry.");
            }
        }

        var text = new System.Net.NetworkCredential(string.Empty, secureToken).Password;
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("AccessToken must not be empty or contain whitespace.", "AccessToken");
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            var key = $"supplied:{Convert.ToHexString(SHA256.HashData(bytes))}";
            // A bare SecureString has no known expiry, so its cache entry must not be reused.
            return (key, new AccessToken(text, expiresOn), cacheToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
