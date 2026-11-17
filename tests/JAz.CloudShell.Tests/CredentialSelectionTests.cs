using Azure.Core;
using Azure.Identity;
using Xunit;

namespace JAz.CloudShell.Tests;

public class CredentialSelectionTests
{
    [Fact]
    public void CredentialSource_ExposesSupportedValues()
    {
        Assert.Equal(["Environment", "VSCode", "AzureCLI", "WAM"], Enum.GetNames<AzureCredentialSource>());
    }

    [Fact]
    public void DefaultCredential_UsesCloudShellCompatibleOrder()
    {
        var credentials = DefaultAuthentication.CreateDefaultCredentials(null);
        var expectedNames = new List<string>
        {
            "EnvironmentCredential",
            "VisualStudioCodeCredential",
            "AzureCliCredential",
        };
        if (OperatingSystem.IsWindows())
        {
            expectedNames.Add("BrokerCredential");
        }

        Assert.Equal(expectedNames, credentials.Select(credential => credential.Name));
    }

    [Theory]
    [InlineData(AzureCredentialSource.Environment, "EnvironmentCredential")]
    [InlineData(AzureCredentialSource.VSCode, "VisualStudioCodeCredential")]
    [InlineData(AzureCredentialSource.AzureCLI, "AzureCliCredential")]
    [InlineData(AzureCredentialSource.WAM, "BrokerCredential")]
    public void SelectedCredentialSource_CreatesOnlyThatCredential(AzureCredentialSource source, string expectedName)
    {
        if (source == AzureCredentialSource.WAM && !OperatingSystem.IsWindows())
        {
            Assert.Throws<CredentialUnavailableException>(() => DefaultAuthentication.CreateDefaultCredentials(null, source));
            return;
        }

        var credentials = DefaultAuthentication.CreateDefaultCredentials(null, source);
        Assert.Equal([expectedName], credentials.Select(credential => credential.Name));
    }

    [Fact]
    public async Task TokenAcquisition_LogsEachAttemptAndContinuesWhenCredentialIsUnavailable()
    {
        var calls = new List<string>();
        var messages = new List<string>();
        var credentials = new (string Name, TokenCredential Credential)[]
        {
            ("EnvironmentCredential", new StubCredential((_, _) =>
            {
                calls.Add("EnvironmentCredential");
                throw new CredentialUnavailableException("No environment credentials.");
            })),
            ("VisualStudioCodeCredential", new StubCredential((_, _) =>
            {
                calls.Add("VisualStudioCodeCredential");
                throw new CredentialUnavailableException("VS Code is not signed in.");
            })),
            ("AzureCliCredential", new StubCredential((_, _) =>
            {
                calls.Add("AzureCliCredential");
                return ValueTask.FromResult(new AccessToken("cli-token", DateTimeOffset.UtcNow.AddHours(1)));
            })),
        };

        var token = await DefaultAuthentication.GetTokenAsync(
            credentials, new TokenRequestContext(["scope"]), messages.Add, CancellationToken.None);

        Assert.Equal("cli-token", token.Token);
        Assert.Equal(credentials.Select(item => item.Name), calls);
        Assert.Contains(messages, message => message.Contains("Checking EnvironmentCredential", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("Checking VisualStudioCodeCredential", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("Checking AzureCliCredential", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TokenAcquisition_DoesNotFallbackAfterAuthenticationFailure()
    {
        var calls = new List<string>();
        var credentials = new (string Name, TokenCredential Credential)[]
        {
            ("EnvironmentCredential", new StubCredential((_, _) =>
            {
                calls.Add("EnvironmentCredential");
                throw new AuthenticationFailedException("Invalid environment credentials.");
            })),
            ("AzureCliCredential", new StubCredential((_, _) =>
            {
                calls.Add("AzureCliCredential");
                return ValueTask.FromResult(new AccessToken("cli-token", DateTimeOffset.UtcNow.AddHours(1)));
            })),
        };

        await Assert.ThrowsAsync<AuthenticationFailedException>(async () =>
            await DefaultAuthentication.GetTokenAsync(
                credentials, new TokenRequestContext(["scope"]), _ => { }, CancellationToken.None));

        Assert.Equal(["EnvironmentCredential"], calls);
    }

    private sealed class StubCredential(Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> getToken) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            getToken(requestContext, cancellationToken);
    }
}
