using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Azure.Core;
using Azure.Identity;

namespace JAz.CloudShell;

/// <summary>
/// <para type="synopsis">Creates a PowerShell remoting session to Azure Cloud Shell.</para>
/// <para type="description">Authenticates with DefaultAzureCredential, opens an Azure Cloud Shell bash terminal over its
/// WebSocket (reusing the console and token of earlier sessions in this process when possible), starts PowerShell in server mode (pwsh -s) in that terminal, and carries PowerShell remoting over the
/// server's stdin and stdout. Use the returned PSSession with Invoke-Command, Enter-PSSession, and Remove-PSSession.</para>
/// </summary>
[Cmdlet(VerbsCommon.New, "AzCloudShellSession")]
[OutputType(typeof(PSSession))]
public sealed class NewAzCloudShellSessionCommand : PSCmdlet
{
    private const int TerminalColumns = 240;
    private const int TerminalRows = 50;

    /// <summary>Test hook: connect to this terminal WebSocket instead of provisioning Azure Cloud Shell.</summary>
    internal static Uri? WebSocketUriOverride;

    private readonly CancellationTokenSource _stop = new();

    /// <summary>Optional name for the new PSSession.</summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? Name { get; set; }

    /// <summary>Optional Microsoft Entra tenant ID used by DefaultAzureCredential.</summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? TenantId { get; set; }

    /// <summary>Maximum seconds allowed for authentication, Cloud Shell provisioning, bootstrap, and session opening.</summary>
    [Parameter]
    [ValidateRange(1, 3600)]
    public int ConnectingTimeout { get; set; } = 300;

    protected override void ProcessRecord()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectingTimeout));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _stop.Token);
        var ct = linked.Token;
        var messages = new BlockingCollection<string>();
        var progress = new ProgressRecord(0x4A5A, "Connecting to Azure Cloud Shell", "Starting");

        var connect = Task.Run(() => ConnectAsync(messages.Add, ct), ct);
        CloudShellBridgeChannel? channel = null;
        Runspace? runspace = null;
        try
        {
            PumpMessages(connect, messages, progress);
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
            progress.RecordType = ProgressRecordType.Completed;
            WriteProgress(progress);
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
                TimeoutException => ErrorCategory.OperationTimeout,
                CloudShellException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized } => ErrorCategory.PermissionDenied,
                _ => ErrorCategory.ConnectionError,
            };
            var message = ex is AuthenticationFailedException or CredentialUnavailableException
                ? $"DefaultAzureCredential could not obtain an Azure token. Sign in with Azure CLI (az login), Azure PowerShell (Connect-AzAccount), or another supported developer credential. {ex.Message}"
                : ex.Message;
            ThrowTerminatingError(new ErrorRecord(new CloudShellException("Connect", message, inner: ex), "AzCloudShellConnectFailed", category, null));
        }
        finally
        {
            runspace?.Dispose();
            channel?.Dispose();
            if (!connect.IsCompleted)
            {
                // Make sure a late-completing connection is not leaked.
                _ = connect.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }
    }

    protected override void StopProcessing() => _stop.Cancel();

    private void PumpMessages(Task connect, BlockingCollection<string> messages, ProgressRecord progress)
    {
        while (!connect.IsCompleted || messages.Count > 0)
        {
            if (messages.TryTake(out var message, 100))
            {
                WriteVerbose(message);
                progress.StatusDescription = message;
                progress.PercentComplete = AdvancePercent(progress.PercentComplete, message);
                WriteProgress(progress);
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

    private async Task<CloudShellBridgeChannel> ConnectAsync(Action<string> log, CancellationToken ct)
    {
        if (WebSocketUriOverride is { } overrideUri)
        {
            return await OpenTerminalAsync(overrideUri, log, ct).ConfigureAwait(false);
        }

        var cacheKey = TenantId ?? string.Empty;
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(100) };
        var credential = new Lazy<DefaultAzureCredential>(() => new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = TenantId }));
        ValueTask<AccessToken> GetToken(CancellationToken token) =>
            credential.Value.GetTokenAsync(new TokenRequestContext([CloudShellClient.ArmScope], tenantId: TenantId), token);

        while (true)
        {
            var (webSocketUri, reused) = await CloudShellConsoleCache.CreateTerminalAsync(cacheKey, GetToken, http, TerminalColumns, TerminalRows, log, ct).ConfigureAwait(false);
            try
            {
                return await OpenTerminalAsync(webSocketUri, log, ct).ConfigureAwait(false);
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
