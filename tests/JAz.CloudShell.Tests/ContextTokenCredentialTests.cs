using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Xunit;

namespace JAz.CloudShell.Tests;

public class ContextTokenCredentialTests
{
    private const string Tenant = "dc220ecf-dce5-4db4-b96b-a73950d1c388";
    private const string Account = "a432876f-03eb-4328-841e-021df3d818a9";
    private const string OtherId = "1b2f3dd1-0010-433b-8714-57bff562b5c5";

    [Fact]
    public void BrokerRecord_PinsTheContextAccountAndTenant()
    {
        var record = ContextTokenCredential.CreateRecord("selected@example.test", $"{Account}.{Tenant}", Tenant);
        Assert.Equal("selected@example.test", record.Username);
        Assert.Equal($"{Account}.{Tenant}", record.HomeAccountId);
        Assert.Equal(Tenant, record.TenantId);
        Assert.Equal("04b07795-8ddb-461a-bbee-02f9e1bf7b46", record.ClientId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingIdentity_UsesBrokerOrVerifiedCliToken(bool brokerUnavailable)
    {
        var calls = new List<string>();
        var expected = Token(Tenant, Account, "powershell");
        var compatible = Token(Tenant, Account, "compatible");
        var broker = new StubCredential((request, _) =>
        {
            calls.Add("broker");
            Assert.Equal(Tenant, request.TenantId);
            Assert.Equal([CloudShellClient.ArmScope], request.Scopes);
            if (brokerUnavailable)
            {
                throw new AuthenticationRequiredException("Interaction is needed.", request);
            }

            return ValueTask.FromResult(compatible);
        });
        var cli = new StubCredential((request, _) =>
        {
            calls.Add("cli");
            Assert.Equal(Tenant, request.TenantId);
            return ValueTask.FromResult(compatible);
        });
        var result = await ContextTokenCredential.GetTokenAsync(
            expected, Tenant, broker, cli, _ => { }, CancellationToken.None);
        Assert.Equal(compatible.Token, result.Token);
        Assert.Equal(brokerUnavailable ? ["broker", "cli"] : new[] { "broker" }, calls);
    }

    [Theory]
    [InlineData(Tenant, OtherId)]
    [InlineData(OtherId, Account)]
    public async Task WrongBrokerIdentity_IsRejectedWithoutTryingAnotherAccount(string tenant, string account)
    {
        var broker = new StubCredential((_, _) => ValueTask.FromResult(Token(tenant, account)));
        var cli = new StubCredential((_, _) => throw new InvalidOperationException("Must not switch identity."));
        var error = await Assert.ThrowsAsync<AuthenticationFailedException>(async () =>
            await ContextTokenCredential.GetTokenAsync(
                Token(Tenant, Account), Tenant, broker, cli, _ => { }, CancellationToken.None));
        Assert.Contains("did not match", error.Message);
    }

    [Fact]
    public async Task WrongCliIdentity_IsRejectedWhenBrokerIsUnavailable()
    {
        var cli = new StubCredential((_, _) => ValueTask.FromResult(Token(Tenant, OtherId)));
        await Assert.ThrowsAsync<AuthenticationFailedException>(async () =>
            await ContextTokenCredential.GetTokenAsync(
                Token(Tenant, Account), Tenant, null, cli, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredCompatibleToken_IsRejected()
    {
        var expired = new AccessToken(Token(Tenant, Account).Token, DateTimeOffset.UtcNow.AddMinutes(-1));
        var cli = new StubCredential((_, _) => ValueTask.FromResult(expired));
        await Assert.ThrowsAsync<AuthenticationFailedException>(async () =>
            await ContextTokenCredential.GetTokenAsync(
                Token(Tenant, Account), Tenant, null, cli, _ => { }, CancellationToken.None));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("tenant")]
    [InlineData("missing")]
    public async Task UnverifiableContextToken_DoesNotRequestAnotherCredential(string failure)
    {
        var expected = failure switch
        {
            "tenant" => Token(OtherId, Account),
            "missing" => new AccessToken("header.e30.signature", DateTimeOffset.UtcNow.AddHours(1)),
            _ => new AccessToken("not-a-jwt", DateTimeOffset.UtcNow.AddHours(1)),
        };
        var credential = new StubCredential((_, _) => throw new InvalidOperationException("Must not acquire another token."));
        await Assert.ThrowsAsync<AuthenticationFailedException>(async () =>
            await ContextTokenCredential.GetTokenAsync(
                expected, Tenant, credential, credential, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledBrokerRequest_DoesNotFallBackToCli()
    {
        using var cts = new CancellationTokenSource();
        var broker = new StubCredential((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var cli = new StubCredential((_, _) => throw new InvalidOperationException("Must not fall back after cancellation."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ContextTokenCredential.GetTokenAsync(
                Token(Tenant, Account), Tenant, broker, cli, _ => { }, cts.Token));
    }

    private static AccessToken Token(string tenant, string account, string client = "compatible")
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { tid = tenant, oid = account, appid = client });
        return new AccessToken($"header.{Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.signature",
            DateTimeOffset.UtcNow.AddHours(1));
    }

    private sealed class StubCredential(Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> getToken) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            getToken(requestContext, cancellationToken);
    }
}
