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
3. **Server mode.** Once PSReadLine is reading input, the cmdlet types one line at the interactive Cloud Shell prompt.
   That line runs a short `bash -c` launcher that:
   - switches the TTY to raw mode with no echo, so PSRP passes through unchanged
   - starts `pwsh -NoLogo -NoProfile -s` (PowerShell's built-in remoting server mode), with stdin and stdout/stderr
     connected through pipes so .NET never treats them as a terminal
   - discards terminal input until a nonce-tagged start line, so stray terminal replies can't reach the server
   - ends the server's stdin on a nonce-tagged stop line, then restores the TTY and prints an exit marker
4. **Transport.** A `RunspaceConnectionInfo` / `ClientSessionTransportManagerBase` pair, following the pattern in
   [PaulHigin/NamedPipeConnection](https://github.com/PaulHigin/NamedPipeConnection), writes PSRP lines to the
   server's stdin and reads PSRP lines from its stdout. Other server output (stderr is merged into the same stream) is
   kept and included in the error if the server exits.

## Requirements

- PowerShell 7.6 or later (.NET 10) locally, on Windows, Linux, or macOS.
- A Cloud Shell account that is already onboarded in the public Azure cloud. Open https://shell.azure.com once to set it
  up. The module never onboards an account or changes your Cloud Shell settings.

## Parameters

| Parameter | Description |
| --- | --- |
| `-Name` | PSSession name. |
| `-TenantId` | Tenant for `DefaultAzureCredential`. |
| `-ConnectingTimeout` | Seconds for the whole connect sequence (default 300, range 1–3600). |

## Limitations

- The session runs in a separate `pwsh -s` process started from one Cloud Shell terminal, so `$PID` is that
  process and the Cloud Shell profile isn't loaded. Closing the session ends the server and returns the terminal to
  its prompt. If the WebSocket drops, the session becomes Broken. It does not reconnect, and
  commands are never replayed.
- Each session uses one Cloud Shell terminal. Cloud Shell's own idle timeout (about 20 minutes) still applies.
- Sovereign clouds aren't supported.
- Starting a fresh Cloud Shell usually takes about a minute, mostly while its `pwsh` loads the profile. The cmdlet
  waits for PSReadLine to start reading input before it types anything, and it answers the terminal's cursor-position
  queries (`ESC[6n`) the way a terminal emulator would.

## Validation

The module was tested live against public Azure Cloud Shell (PowerShell 7.6.9, Linux), with Windows as the local
client. These all worked:
- `$PID` (the `pwsh -s` process) and `$using:` with Unicode text
- 20,000 lines of output (about 4 s) and a 300 KB argument (under 1 s)
- remote errors, native commands, and the Cloud Shell Az context
- `Remove-PSSession`

The automated end-to-end test emulates the terminal and the launcher, and runs PSRP against a real `pwsh -s` on
Windows. The real `bash`/`stty` launcher is only exercised in Cloud Shell. Linux and macOS clients haven't been tested.

## Build and test

```powershell
./build.ps1             # publish to out/JAz.CloudShell and run all tests
./build.ps1 -SkipTests
```

The tests need `pwsh` on `PATH`. The end-to-end test starts a loopback WebSocket "terminal" backed by a real `pwsh`
server-mode process and runs `New-AzCloudShellSession` and `Invoke-Command` against it.
