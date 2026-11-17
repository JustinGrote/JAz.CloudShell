using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using Azure.Core;
using Azure.Identity;

namespace JAz.CloudShell;

internal static class AzContextAuthentication
{
    // Keep Az.Accounts optional: validate its interface at runtime rather than shipping a second copy of its assemblies.
    internal static PSObject ConvertProfile(PSObject context)
    {
        var type = new PSTypeName("Microsoft.Azure.Commands.Common.Authentication.Abstractions.Core.IAzureContextContainer").Type;
        if (type is null)
        {
            throw new ArgumentException("Context requires Az.Accounts. Import Az.Accounts and supply an IAzureContextContainer.", nameof(context));
        }

        return PSObject.AsPSObject(LanguagePrimitives.ConvertTo(context.BaseObject, type));
    }

    internal static string GetCacheKey(PSObject profile)
    {
        var context = PSObject.AsPSObject(profile.Properties["DefaultContext"]?.Value
            ?? throw new ArgumentException("The supplied IAzureContextContainer has no default context.", nameof(profile)));
        var account = GetString(context.Properties["Account"]?.Value, "Id");
        var tenant = GetString(context.Properties["Tenant"]?.Value, "Id");
        var environment = GetString(context.Properties["Environment"]?.Value, "Name");
        if (!string.Equals(environment, "AzureCloud", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only public Azure (AzureCloud) contexts are supported.", nameof(context));
        }

        return $"context:{account.Length}:{account}:{tenant}";
    }

    private static string GetString(object? value, string property)
    {
        if (value is not null && PSObject.AsPSObject(value).Properties[property]?.Value is string text && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new ArgumentException($"Context must contain a nonempty {property} for its account, tenant, and environment.", "Context");
    }

    internal static AccessToken GetToken(PSObject context, CancellationToken ct)
    {
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddCommand("Az.Accounts\\Get-AzAccessToken")
            .AddParameter("DefaultProfile", context.BaseObject)
            .AddParameter("ResourceTypeName", "Arm")
            .AddParameter("AsSecureString")
            .AddParameter("ErrorAction", ActionPreference.Stop);
        using var registration = ct.Register(ps.Stop);
        PSObject[] output;
        try
        {
            output = ps.Invoke().ToArray();
        }
        catch (PipelineStoppedException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (RuntimeException ex) when (ex is not PipelineStoppedException)
        {
            throw new AuthenticationFailedException($"Az.Accounts could not obtain a token for the supplied profile: {ex.Message}", ex);
        }

        ct.ThrowIfCancellationRequested();
        if (ps.HadErrors)
        {
            var error = ps.Streams.Error[0];
            throw new AuthenticationFailedException($"Az.Accounts could not obtain a token: {error}", error.Exception);
        }

        var tokenValue = output.Length == 1 ? output[0].Properties["Token"]?.Value : null;
        var expiryValue = output.Length == 1 ? output[0].Properties["ExpiresOn"]?.Value : null;
        if (tokenValue is null || expiryValue is null ||
            PSObject.AsPSObject(tokenValue).BaseObject is not SecureString secureToken ||
            PSObject.AsPSObject(expiryValue).BaseObject is not DateTimeOffset expiresOn)
        {
            throw new AuthenticationFailedException("Az.Accounts did not return a valid secure access token for the supplied context.");
        }

        var token = new System.Net.NetworkCredential(string.Empty, secureToken).Password;
        if (string.IsNullOrWhiteSpace(token) || expiresOn <= DateTimeOffset.UtcNow)
        {
            throw new AuthenticationFailedException("Az.Accounts returned an empty or expired access token for the supplied context.");
        }

        return new AccessToken(token, expiresOn);
    }
}
