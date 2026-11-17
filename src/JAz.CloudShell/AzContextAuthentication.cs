using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Commands.Common.Authentication.Abstractions.Core;
using Microsoft.Azure.Commands.Profile.Models;

namespace JAz.CloudShell;

internal static class AzContextAuthentication
{
    internal static IAzureContextContainer ConvertProfile(PSObject context) =>
        LanguagePrimitives.ConvertTo<IAzureContextContainer>(context.BaseObject);

    internal static string GetCacheKey(IAzureContextContainer profile)
    {
        var context = profile.DefaultContext
            ?? throw new ArgumentException("The supplied IAzureContextContainer has no default context.", nameof(profile));
        var account = RequireValue(context.Account?.Id, "account ID");
        var tenant = RequireValue(context.Tenant?.Id, "tenant ID");
        var environment = RequireValue(context.Environment?.Name, "environment name");
        if (!string.Equals(environment, "AzureCloud", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only public Azure (AzureCloud) contexts are supported.", nameof(context));
        }

        return $"context:{account.Length}:{account}:{tenant}";
    }

    private static string RequireValue(string? value, string property)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new ArgumentException($"Context must contain a nonempty {property}.", "Context");
    }

    internal static AccessToken GetToken(IAzureContextContainer context, CancellationToken ct)
    {
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddCommand("Az.Accounts\\Get-AzAccessToken")
            .AddParameter("DefaultProfile", context)
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

        if (output.Length != 1 || output[0].BaseObject is not PSSecureAccessToken result || result.Token is null)
        {
            throw new AuthenticationFailedException("Az.Accounts did not return a valid secure access token for the supplied context.");
        }

        var token = new System.Net.NetworkCredential(string.Empty, result.Token).Password;
        if (string.IsNullOrWhiteSpace(token) || result.ExpiresOn <= DateTimeOffset.UtcNow)
        {
            throw new AuthenticationFailedException("Az.Accounts returned an empty or expired access token for the supplied context.");
        }

        return new AccessToken(token, result.ExpiresOn);
    }
}
