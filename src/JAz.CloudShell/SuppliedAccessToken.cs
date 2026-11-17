using System.Security;
using Azure.Core;

namespace JAz.CloudShell;

internal static class SuppliedAccessToken
{
    internal static AccessToken Read(SecureString secureToken)
    {
        var text = new System.Net.NetworkCredential(string.Empty, secureToken).Password;
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("AccessToken must contain a nonempty token without whitespace.", "AccessToken");
        }

        return new AccessToken(text, DateTimeOffset.MinValue);
    }
}
