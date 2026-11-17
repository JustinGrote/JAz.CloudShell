using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Remoting;
using System.Management.Automation.Remoting.Client;
using System.Management.Automation.Runspaces;
using System.Text;

namespace JAz.CloudShell;

/// <summary>Connection information for a PowerShell runspace bridged over an Azure Cloud Shell terminal.</summary>
public sealed class AzCloudShellConnectionInfo : RunspaceConnectionInfo
{
    internal AzCloudShellConnectionInfo(CloudShellBridgeChannel channel) => Channel = channel;

    internal CloudShellBridgeChannel Channel { get; }

    public override string ComputerName
    {
        get => "AzCloudShell";
        set => throw new NotSupportedException();
    }

    public override PSCredential Credential
    {
        get => null!;
        set => throw new NotSupportedException();
    }

    public override AuthenticationMechanism AuthenticationMechanism
    {
        get => AuthenticationMechanism.Default;
        set => throw new NotSupportedException();
    }

    public override string CertificateThumbprint
    {
        get => string.Empty;
        set => throw new NotSupportedException();
    }

    // PowerShell clones connection info when it creates the runspace. The clone intentionally refers to the same
    // single-use bridge; the channel itself rejects a second transport binding.
    public override RunspaceConnectionInfo Clone() => new AzCloudShellConnectionInfo(Channel);

    public override BaseClientSessionTransportManager CreateClientSessionTransportManager(Guid instanceId, string sessionName, PSRemotingCryptoHelper cryptoHelper) =>
        new AzCloudShellClientSessionTransportManager(this, instanceId, cryptoHelper);
}

internal sealed class AzCloudShellClientSessionTransportManager : ClientSessionTransportManagerBase
{
    private readonly AzCloudShellConnectionInfo _connectionInfo;
    private int _errorRaised;

    internal AzCloudShellClientSessionTransportManager(AzCloudShellConnectionInfo connectionInfo, Guid runspaceId, PSRemotingCryptoHelper cryptoHelper)
        : base(runspaceId, cryptoHelper)
    {
        _connectionInfo = connectionInfo;
    }

    public override void CreateAsync()
    {
        var channel = _connectionInfo.Channel;
        SetMessageWriter(new BridgeTextWriter(channel));
        channel.Bind(HandleDataReceived, RaiseTransportError);

        var starter = new Thread(() =>
        {
            try
            {
                SendOneItem();
            }
            catch (Exception ex)
            {
                RaiseTransportError(ex);
            }
        })
        {
            IsBackground = true,
            Name = "JAz.CloudShell PSRP writer start",
        };
        starter.Start();
    }

    protected override void CleanupConnection() => _connectionInfo.Channel.Dispose();

    protected override void Dispose(bool isDisposing)
    {
        base.Dispose(isDisposing);
        if (isDisposing)
        {
            _connectionInfo.Channel.Dispose();
        }
    }

    private void RaiseTransportError(Exception ex)
    {
        if (Interlocked.Exchange(ref _errorRaised, 1) != 0)
        {
            return;
        }

        RaiseErrorHandler(new TransportErrorOccuredEventArgs(
            new PSRemotingTransportException($"The Azure Cloud Shell transport failed: {ex.Message}", ex),
            TransportMethodEnum.ReceiveShellOutputEx));
        _connectionInfo.Channel.Dispose();
    }

    /// <summary>Buffers PSRP text and sends each completed line batch through the bridge.</summary>
    private sealed class BridgeTextWriter(CloudShellBridgeChannel channel) : TextWriter
    {
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string NewLine
        {
            get => "\n";
            set { }
        }

        public override void Write(char value)
        {
            _buffer.Append(value);
            if (value == '\n')
            {
                Flush();
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            _buffer.Append(value);
            if (value.Contains('\n'))
            {
                Flush();
            }
        }

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

        public override void WriteLine(string? value)
        {
            _buffer.Append(value).Append('\n');
            Flush();
        }

        public override void Flush()
        {
            if (_buffer.Length == 0)
            {
                return;
            }

            var text = _buffer.ToString();
            _buffer.Clear();
            channel.WriteData(text);
        }
    }
}
