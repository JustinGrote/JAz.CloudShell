using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Commands.Common.Authentication.Abstractions.Core;

namespace JAz.CloudShell;

/// <summary>
/// <para type="synopsis">Creates a PowerShell remoting session to Azure Cloud Shell.</para>
/// <para type="description">Authenticates with DefaultAzureCredential, supplied Az.Accounts contexts, or a supplied secure access token, opens an Azure Cloud Shell bash terminal over its
/// WebSocket (reusing the console and token of earlier sessions in this process when possible), starts PowerShell in server mode (pwsh -s) in that terminal, and carries PowerShell remoting over the
/// server's stdin and stdout. Use the returned PSSession with Invoke-Command, Enter-PSSession, and Remove-PSSession.</para>
/// </summary>
[Cmdlet(VerbsCommon.New, "JAzCloudShellSession", DefaultParameterSetName = "Default")]
[OutputType(typeof(PSSession))]
public sealed class NewJAzCloudShellSessionCommand : PSCmdlet
{
    private const int TerminalColumns = 240;
    private const int TerminalRows = 50;

    /// <summary>Test hook: connect to this terminal WebSocket instead of provisioning Azure Cloud Shell.</summary>
    internal static Uri? WebSocketUriOverride;
    internal static Func<HttpClient>? HttpClientFactoryOverride;

    private readonly CancellationTokenSource _stop = new();

    /// <summary>Optional name for the new PSSession.</summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? Name { get; set; }

    /// <summary>Optional Microsoft Entra tenant ID used by DefaultAzureCredential.</summary>
    [Parameter(ParameterSetName = "Default")]
    [ValidateNotNullOrEmpty]
    public string? TenantId { get; set; }

    /// <summary>Skip the silent WAM check and use DefaultAzureCredential directly.</summary>
    [Parameter(ParameterSetName = "Default")]
    public SwitchParameter NoWAM { get; set; }

    /// <summary>Az.Accounts IAzureContextContainer objects identifying the accounts and tenants to connect.</summary>
    [Parameter(ParameterSetName = "Context", Mandatory = true, ValueFromPipeline = true)]
    [ValidateNotNullOrEmpty]
    public PSObject[]? Context { get; set; }

    /// <summary>An Az.Accounts PSSecureAccessToken or a SecureString containing an ARM access token.</summary>
    [Parameter(ParameterSetName = "AccessToken", Mandatory = true)]
    [ValidateNotNull]
    public PSObject? AccessToken { get; set; }

    /// <summary>Maximum seconds allowed for authentication, Cloud Shell provisioning, bootstrap, and session opening.</summary>
    [Parameter]
    [ValidateRange(1, 3600)]
    public int ConnectingTimeout { get; set; } = 300;

    protected override void ProcessRecord()
    {
        if (Context is null)
        {
            CreateSession(null);
            return;
        }

        foreach (var context in Context)
        {
            CreateSession(context);
        }
    }

    private void CreateSession(PSObject? context)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectingTimeout));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _stop.Token);
        var ct = linked.Token;
        using var messages = new BlockingCollection<Action>();
        var progress = new ProgressRecord(0x4A5A, "Connecting to Azure Cloud Shell", "Starting") { PercentComplete = 0 };
        IAzureContextContainer? profile = null;
        void Log(string message) => messages.Add(() =>
        {
            WriteVerbose(message);
            progress.StatusDescription = message;
            progress.PercentComplete = AdvancePercent(progress.PercentComplete, message);
            WriteProgress(progress);
        }, ct);

        ValueTask<AccessToken> GetContextToken(CancellationToken token)
        {
            var request = new TaskCompletionSource<AccessToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Nested PowerShell commands must run on the pipeline thread, not the connection worker.
            messages.Add(() =>
            {
                if (token.IsCancellationRequested)
                {
                    request.TrySetCanceled(token);
                    return;
                }

                try
                {
                    request.TrySetResult(AzContextAuthentication.GetToken(profile!, token));
                }
                catch (Exception ex)
                {
                    request.TrySetException(ex);
                }
            }, token);
            return new ValueTask<AccessToken>(request.Task.WaitAsync(token));
        }

        Task<CloudShellBridgeChannel>? connect = null;
        CloudShellBridgeChannel? channel = null;
        Runspace? runspace = null;
        try
        {
            var cacheKey = $"default:{(NoWAM.IsPresent ? "no-wam" : "wam")}:{TenantId}";
            var tokenSource = "DefaultAzureCredential";
            Func<CancellationToken, ValueTask<Azure.Core.AccessToken>>? getToken = null;
            var cacheToken = true;
            if (AccessToken is not null)
            {
                var supplied = SuppliedAccessToken.Read(AccessToken);
                cacheKey = supplied.CacheKey;
                getToken = _ => ValueTask.FromResult(supplied.Token);
                tokenSource = "the supplied access token";
                cacheToken = supplied.CacheToken;
            }
            else if (context is not null)
            {
                profile = AzContextAuthentication.ConvertProfile(context);
                cacheKey = AzContextAuthentication.GetCacheKey(profile);
                getToken = GetContextToken;
                tokenSource = "Az.Accounts for the supplied context";
            }

            connect = Task.Run(() => ConnectAsync(cacheKey, getToken, tokenSource, cacheToken, Log, ct), ct);
            PumpMessages(connect, messages);
            try
            {
                channel = connect.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Connecting to Azure Cloud Shell did not complete within {ConnectingTimeout} seconds.");
            }

            progress.StatusDescription = "Opening the PowerShell remoting session";
            progress.PercentComplete = 90;
            WriteProgress(progress);
            var connectionInfo = new AzCloudShellConnectionInfo(channel);
            runspace = RunspaceFactory.CreateRunspace(connectionInfo, Host, TypeTable.LoadDefaultTypeFiles(), null, Name);
            using var opened = new ManualResetEventSlim();
            runspace.StateChanged += (_, e) =>
            {
                if (e.RunspaceStateInfo.State is RunspaceState.Opened or RunspaceState.Broken or RunspaceState.Closed)
                {
                    opened.Set();
                }
            };
            runspace.OpenAsync();
            try
            {
                opened.Wait(ct);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Opening the Azure Cloud Shell remoting session did not complete within {ConnectingTimeout} seconds.");
            }

            var state = runspace.RunspaceStateInfo;
            if (state.State != RunspaceState.Opened)
            {
                throw new CloudShellException("Session", $"The Azure Cloud Shell remoting session could not be opened: {state.Reason?.Message ?? state.State.ToString()}", inner: state.Reason);
            }

            progress.PercentComplete = 100;
            progress.StatusDescription = "Connected";
            WriteObject(PSSession.Create(runspace, "AzCloudShell", this));
            runspace = null;
            channel = null;
        }
        catch (Exception ex) when (ex is not PipelineStoppedException)
        {
            if (_stop.IsCancellationRequested)
            {
                throw new PipelineStoppedException();
            }

            var category = ex switch
            {
                AuthenticationFailedException or CredentialUnavailableException => ErrorCategory.AuthenticationError,
                ArgumentException => ErrorCategory.InvalidArgument,
                TimeoutException => ErrorCategory.OperationTimeout,
                CloudShellException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized } => ErrorCategory.PermissionDenied,
                _ => ErrorCategory.ConnectionError,
            };
            var message = context is null && AccessToken is null && ex is (AuthenticationFailedException or CredentialUnavailableException)
                ? $"DefaultAzureCredential could not obtain an Azure token. Sign in with Azure CLI (az login), Azure PowerShell (Connect-AzAccount), or another supported developer credential. {ex.Message}"
                : ex.Message;
            var error = new ErrorRecord(new CloudShellException("Connect", message, inner: ex), "AzCloudShellConnectFailed", category, context);
            if (context is null)
            {
                ThrowTerminatingError(error);
            }
            else
            {
                WriteError(error);
            }
        }
        finally
        {
            runspace?.Dispose();
            channel?.Dispose();
            progress.RecordType = ProgressRecordType.Completed;
            WriteProgress(progress);
            if (connect is { IsCompleted: false })
            {
                // Make sure a late-completing connection is not leaked.
                _ = connect.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }
    }

    protected override void StopProcessing() => _stop.Cancel();

    private static void PumpMessages(Task connect, BlockingCollection<Action> messages)
    {
        while (!connect.IsCompleted || messages.Count > 0)
        {
            if (messages.TryTake(out var action, 100))
            {
                action();
            }
        }
    }

    // Retry and fallback messages keep the current value so the bar never moves backwards.
    internal static int AdvancePercent(int current, string message)
    {
        var phase = message switch
        {
            _ when message.StartsWith("Acquiring an Azure Resource Manager token", StringComparison.Ordinal) => 5,
            _ when message.StartsWith("Reading Cloud Shell user settings", StringComparison.Ordinal) => 15,
            _ when message.StartsWith("Requesting a Cloud Shell console", StringComparison.Ordinal) => 25,
            _ when message.StartsWith("Reusing the existing Cloud Shell console", StringComparison.Ordinal) => 30,
            _ when message.StartsWith("Creating a bash terminal", StringComparison.Ordinal) => 40,
            _ when message.StartsWith("Connecting to the Cloud Shell terminal WebSocket", StringComparison.Ordinal) => 50,
            _ when message.StartsWith("Waiting for the Cloud Shell bash prompt", StringComparison.Ordinal) => 60,
            _ when message.StartsWith("Starting PowerShell in server mode", StringComparison.Ordinal) => 75,
            _ => -1,
        };
        return Math.Max(current, phase);
    }

    private async Task<CloudShellBridgeChannel> ConnectAsync(
        string cacheKey,
        Func<CancellationToken, ValueTask<AccessToken>>? getSuppliedToken,
        string tokenSource,
        bool cacheToken,
        Action<string> log,
        CancellationToken ct)
    {
        if (WebSocketUriOverride is { } overrideUri && HttpClientFactoryOverride is null)
        {
            return await OpenTerminalAsync(overrideUri, log, ct).ConfigureAwait(false);
        }

        using var http = HttpClientFactoryOverride?.Invoke()
            ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(100) };
        ValueTask<AccessToken> GetToken(CancellationToken token) =>
            DefaultAuthentication.GetTokenAsync(TenantId, NoWAM.IsPresent, log, token);

        while (true)
        {
            var (webSocketUri, reused) = await CloudShellConsoleCache.CreateTerminalAsync(
                cacheKey, getSuppliedToken ?? GetToken, http, TerminalColumns, TerminalRows, log, ct, tokenSource, cacheToken).ConfigureAwait(false);
            try
            {
                return await OpenTerminalAsync(WebSocketUriOverride ?? webSocketUri, log, ct).ConfigureAwait(false);
            }
            catch (CloudShellException ex) when (reused && !ct.IsCancellationRequested)
            {
                log($"The reused Cloud Shell console did not respond ({ex.Message}); requesting it again.");
                CloudShellConsoleCache.Invalidate(cacheKey);
            }
        }
    }

    private static async Task<CloudShellBridgeChannel> OpenTerminalAsync(Uri webSocketUri, Action<string> log, CancellationToken ct)
    {
        log("Connecting to the Cloud Shell terminal WebSocket.");
        var channel = await CloudShellBridgeChannel.ConnectAsync(webSocketUri, ct).ConfigureAwait(false);
        try
        {
            await Bootstrapper.RunAsync(channel, log, ct).ConfigureAwait(false);
            return channel;
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }
}
