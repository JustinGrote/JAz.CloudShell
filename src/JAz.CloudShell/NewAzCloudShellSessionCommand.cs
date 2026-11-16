using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Azure.Core;
using Azure.Identity;

namespace JAz.CloudShell;

/// <summary>
/// <para type="synopsis">Creates a PowerShell remoting session to Azure Cloud Shell.</para>
/// <para type="description">Authenticates with DefaultAzureCredential, opens an Azure Cloud Shell PowerShell terminal over its
/// WebSocket, discovers the Cloud Shell PowerShell process ($PID), and bridges a PowerShell remoting session to that process's
/// named pipe. Use the returned PSSession with Invoke-Command, Enter-PSSession, and Remove-PSSession.</para>
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
            BootstrapResult result;
            try
            {
                (channel, result) = connect.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Connecting to Azure Cloud Shell did not complete within {ConnectingTimeout} seconds.");
            }

            progress.StatusDescription = "Opening the PowerShell remoting session";
            WriteProgress(progress);
            var connectionInfo = new AzCloudShellConnectionInfo(channel, result.ProcessId, result.PSVersion);
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
                _ = connect.ContinueWith(t => t.Result.Channel.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
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
                WriteProgress(progress);
            }
        }
    }

    private async Task<(CloudShellBridgeChannel Channel, BootstrapResult Result)> ConnectAsync(Action<string> log, CancellationToken ct)
    {
        Uri webSocketUri;
        if (WebSocketUriOverride is { } overrideUri)
        {
            webSocketUri = overrideUri;
        }
        else
        {
            log("Acquiring an Azure Resource Manager token with DefaultAzureCredential.");
            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = TenantId });
            var token = await credential.GetTokenAsync(new TokenRequestContext([CloudShellClient.ArmScope], tenantId: TenantId), ct).ConfigureAwait(false);
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(100) };
            webSocketUri = await new CloudShellClient(http, token.Token, log).ProvisionTerminalAsync(TerminalColumns, TerminalRows, ct).ConfigureAwait(false);
        }

        log("Connecting to the Cloud Shell terminal WebSocket.");
        var channel = await CloudShellBridgeChannel.ConnectAsync(webSocketUri, ct).ConfigureAwait(false);
        try
        {
            var result = await Bootstrapper.RunAsync(channel, log, ct).ConfigureAwait(false);
            return (channel, result);
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }
}
