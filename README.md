# JAz.CloudShell

A custom PowerShell remoting transport for **Azure Cloud Shell**. `New-AzCloudShellSession` returns a normal
`PSSession`, so you can use the usual remoting commands with it:

```powershell
Import-Module ./out/JAz.CloudShell
$cs = New-AzCloudShellSession -Verbose
Invoke-Command -Session $cs { $PID; $PSVersionTable.PSVersion; Get-AzContext }
Enter-PSSession $cs
Remove-PSSession $cs
```

## How it works

1. **Auth.** `DefaultAzureCredential` gets an Azure Resource Manager token. This uses the noninteractive chain only:
   environment variables, workload identity, managed identity, Visual Studio, Azure CLI, Azure PowerShell, and so on.
2. **Provisioning.** The flow is the one Devolutions Terminal uses, and it matches Windows Terminal:
   1. `GET userSettings/cloudconsole`
   2. `PUT consoles/default` with `osType: linux`
   3. `POST {cloudShellUri}/terminals?shell=pwsh`
   4. Connect to the terminal WebSocket (handles both direct and Azure Relay endpoints).
3. **Discovery.** The cmdlet types a probe at the interactive Cloud Shell prompt to read `$PID`, the PowerShell version,
   and the .NET version.
4. **Bridge.** A small helper script is uploaded in chunks. Each chunk is acknowledged, and the whole upload is checked
   by length and SHA-256. The helper is then started as a child `pwsh`. It:
   - switches the TTY to raw mode with no echo
   - connects to the PowerShell remoting named pipe of the `$PID` process
     (`PSHost.<start>.<pid>.<appdomain>.pwsh`, the same endpoint `Enter-PSHostProcess` uses)
   - relays bytes as nonce-tagged base64 frames over the terminal
5. **Transport.** A `RunspaceConnectionInfo` / `ClientSessionTransportManagerBase` pair carries PSRP over that framed
   channel, following the pattern in [PaulHigin/NamedPipeConnection](https://github.com/PaulHigin/NamedPipeConnection).

## Requirements

- PowerShell 7.6 or later (.NET 10) locally, on Windows, Linux, or macOS.
- A Cloud Shell account that is already onboarded in the public Azure cloud. Open https://shell.azure.com once to set it
  up. The module never onboards an account or changes your Cloud Shell settings.
- PowerShell 7.6 or later on .NET 10 in Cloud Shell. The cmdlet checks this and fails clearly if it isn't met.

## Parameters

| Parameter | Description |
| --- | --- |
| `-Name` | PSSession name. |
| `-TenantId` | Tenant for `DefaultAzureCredential`. |
| `-ConnectingTimeout` | Seconds for the whole connect sequence (default 300, range 1–3600). |

## Limitations

- The session is attached to one interactive Cloud Shell terminal. Closing the session ends the bridge helper and
  returns the terminal to its prompt. If the WebSocket drops, the session becomes Broken. It does not reconnect, and
  commands are never replayed.
- Each session uses one Cloud Shell terminal. Cloud Shell's own idle timeout (about 20 minutes) still applies.
- Sovereign clouds aren't supported.
- Starting a fresh Cloud Shell usually takes about a minute, mostly while its `pwsh` loads the profile. The cmdlet
  waits for PSReadLine to start reading input before it types anything, and it answers the terminal's cursor-position
  queries (`ESC[6n`) the way a terminal emulator would.

## Validation

The module was tested live against public Azure Cloud Shell (PowerShell 7.6.9 on .NET 10.0.14, Linux), with Windows
as the local client. These all worked:
- `$PID` and `$using:` with Unicode text
- 20,000 lines of output and a 300 KB argument
- remote errors, native commands, and the existing Az context
- `Remove-PSSession`

The automated end-to-end test drives the real helper, named pipe, and PSRP through a local fake terminal backed by
`pwsh` on Windows. Linux and macOS clients haven't been tested.

## Build and test

```powershell
./build.ps1             # publish to out/JAz.CloudShell and run all tests
./build.ps1 -SkipTests
```

The tests need `pwsh` on `PATH`. The end-to-end test starts a loopback WebSocket "terminal" backed by a real `pwsh`
process and runs `New-AzCloudShellSession` and `Invoke-Command` against it.
