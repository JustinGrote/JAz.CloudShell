using Azure.Core;
using Azure.Identity;
using Xunit;

namespace JAz.CloudShell.Tests;

public class DefaultAuthenticationTests
{
    [Fact]
    public async Task BrokerToken_TakesPriorityWithoutConstructingDefaultCredential()
    {
        var request = new TokenRequestContext([CloudShellClient.ArmScope], tenantId: "tenant");
        using var cts = new CancellationTokenSource();
        var broker = new StubCredential((actual, ct) =>
        {
            Assert.Equal(request.Scopes, actual.Scopes);
            Assert.Equal("tenant", actual.TenantId);
            Assert.Equal(cts.Token, ct);
            return ValueTask.FromResult(new AccessToken("broker-token", DateTimeOffset.UtcNow.AddHours(1)));
        });
        var messages = new List<string>();
        var result = await DefaultAuthentication.GetTokenAsync(
            broker, () => throw new InvalidOperationException("Default credential must not be constructed."),
            request, messages.Add, cts.Token);

        Assert.Equal("broker-token", result.Token);
        Assert.Contains(messages, message => message.StartsWith("Using a token from Windows"));
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("interaction")]
    [InlineData("failed")]
    [InlineData("unsupported")]
    public async Task BrokerCannotAuthenticate_FallsBackToDefaultCredential(string failure)
    {
        var request = new TokenRequestContext([CloudShellClient.ArmScope], tenantId: "tenant");
        var calls = new List<string>();
        TokenCredential? broker = failure == "unsupported" ? null : new StubCredential((_, _) =>
        {
            calls.Add("broker");
            throw failure switch
            {
                "unavailable" => new CredentialUnavailableException("No broker"),
                "interaction" => new AuthenticationRequiredException("Sign-in required", request),
                _ => new AuthenticationFailedException("Broker failure"),
            };
        });
        var messages = new List<string>();
        var result = await DefaultAuthentication.GetTokenAsync(broker, () =>
        {
            calls.Add("default");
            return new StubCredential((actual, _) =>
            {
                Assert.Equal(request.Scopes, actual.Scopes);
                Assert.Equal("tenant", actual.TenantId);
                return ValueTask.FromResult(new AccessToken("default-token", DateTimeOffset.UtcNow.AddHours(1)));
            });
        }, request, messages.Add, CancellationToken.None);

        Assert.Equal("default-token", result.Token);
        Assert.Equal(failure == "unsupported" ? ["default"] : new[] { "broker", "default" }, calls);
        if (broker is not null)
        {
            Assert.Contains(messages, message => message.Contains("trying DefaultAzureCredential"));
        }
    }

    [Fact]
    public async Task CancelledBrokerRequest_DoesNotTryAnotherCredential()
    {
        using var cts = new CancellationTokenSource();
        var broker = new StubCredential((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DefaultAuthentication.GetTokenAsync(
                broker, () => throw new InvalidOperationException("Must not fall back after cancellation."),
                new TokenRequestContext([CloudShellClient.ArmScope]), _ => { }, cts.Token));
    }

    [Fact]
    public async Task DefaultCredentialFailure_IsNotHidden()
    {
        var failure = new AuthenticationFailedException("No default credential succeeded.");
        var error = await Assert.ThrowsAsync<AuthenticationFailedException>(async () =>
            await DefaultAuthentication.GetTokenAsync(null,
                () => new StubCredential((_, _) => throw failure),
                new TokenRequestContext([CloudShellClient.ArmScope]), _ => { }, CancellationToken.None));
        Assert.Same(failure, error);
    }

    private sealed class StubCredential(Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> getToken) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            getToken(requestContext, cancellationToken);
    }
}
