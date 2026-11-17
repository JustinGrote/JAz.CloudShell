# JAz.CloudShell

A custom PowerShell remoting transport for **Azure Cloud Shell**. `New-JAzCloudShellSession` returns a normal
`PSSession`, so you can use the usual remoting commands with it:

```powershell
Import-Module .\Artifacts\publish\JAz.CloudShell\release\JAz.CloudShell.psd1
$cs = New-JAzCloudShellSession -Verbose
Invoke-Command -Session $cs { $PID; $PSVersionTable.PSVersion; Get-AzContext }
Enter-PSSession $cs
Remove-PSSession $cs
```

## How it works

1. **Auth.** 
  Unfortunately, for some reason the Azure PowerShell module client ID is prevented from accessing the Azure Cloud Shell API,
  so the most convenient credentials we would like to use are not available.

  By default, the cmdlet checks `EnvironmentCredential`, then `VisualStudioCodeCredential`,
   then `AzureCliCredential`, and finally the Windows broker on Windows.
   The chain advances when a credential is unavailable; an authentication failure stops the chain.
   `-Verbose` and the progress display report each credential attempt.
   `-CredentialSource` limits authentication to one source: `Environment`, `VSCode`, `AzureCLI`, or `WAM`
   (Windows only); it does not fall back to another source.
   `-TenantId` is passed on the token request and to credentials that support a tenant option.
   With `-Context`, the cmdlet starts with `Get-AzAccessToken -DefaultProfile` for each supplied
   `IAzureContextContainer`. If Cloud Shell rejects the Azure PowerShell client, it silently requests a compatible
   token for the selected account through WAM, or Azure CLI if WAM is unavailable. It checks the token's tenant and
   account object IDs against the Az.Accounts token before contacting Cloud Shell with it. It never switches the
   active Az context or silently connects as another account.
   With `-AccessToken`, it uses the supplied secure ARM access token directly without acquiring another credential.
2. **Provisioning.** The flow is the one Devolutions Terminal uses, and it matches Windows Terminal:
   1. `GET userSettings/cloudconsole`
   2. `PUT consoles/default` with `osType: linux`
   3. `POST {cloudShellUri}/terminals?shell=bash` (a bash terminal starts much faster than one that loads the pwsh profile)
   4. Connect to the terminal WebSocket (handles both direct and Azure Relay endpoints).
3. **Server mode.** Once the bash prompt appears, the cmdlet types one launcher line at it. The launcher:
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
- Az.Accounts 5.3.3 or later (`Install-Module Az.Accounts -MinimumVersion 5.3.3`). It is imported automatically and
  supplies Azure.Identity, Azure.Identity.Broker, and their runtime dependencies; this module does not bundle them.
- A Cloud Shell account that is already onboarded in the public Azure cloud. Open https://shell.azure.com once to set it
  up. The module never onboards an account or changes your Cloud Shell settings.

## Parameters

| Parameter | Description |
| --- | --- |
| `-Name` | PSSession name. |
| `-TenantId` | Optional tenant on the default credential's token request. |
| `-CredentialSource` | Search only `Environment`, `VSCode`, `AzureCLI`, or `WAM` (Windows only). Cannot be combined with `-Context` or `-AccessToken`. |
| `-Context` | One or more Az.Accounts `IAzureContextContainer` profiles. Accepts pipeline input and the same profile conversions as `Get-AzAccessToken -DefaultProfile`. Cannot be combined with `-TenantId`. |
| `-AccessToken` | A `SecureString` containing an ARM access token. Uses a separate parameter set; cannot be combined with `-Context` or `-TenantId`. |
| `-ConnectingTimeout` | Seconds for the whole connect sequence (default 300, range 1–3600). |

### Az.Accounts profiles

```powershell
$profile = Connect-AzAccount
$cs = New-JAzCloudShellSession -Context $profile

# Supply multiple profiles, or stream them through the pipeline:
$sessions = New-JAzCloudShellSession -Context $profiles
$sessions = $profiles | New-JAzCloudShellSession
```

Each profile produces one session using its default context's account and tenant. A failed profile reports an error
and processing continues with the remaining profiles, unless `-ErrorAction Stop` is specified. The connection timeout
applies separately to each profile.

Cloud Shell can reject Azure PowerShell-issued tokens with `ClientNotAllowed`. The cmdlet then uses a compatible
client for the same identity: on Windows it checks WAM using the context's home account ID, without showing a dialog.
If WAM cannot supply a token (or the context has no home account ID), it tries Azure CLI for the context's tenant.
The resulting token must have exactly the same tenant ID and account object ID as the Az.Accounts token. If neither
credential can authenticate that identity, sign in to WAM or run `az login --tenant <tenant-id>` as the selected user,
then retry. A mismatched account fails explicitly. This compatibility path requires a user context; service principal
and managed identity contexts are not substituted with user credentials.

### Supplied access tokens

```powershell
$token = Get-AzAccessToken -ResourceTypeName Arm
$cs = New-JAzCloudShellSession -AccessToken $token.Token
```

Only a `SecureString` is accepted; a `PSSecureAccessToken` object must be passed through its `Token` property.
Secure strings have no expiry metadata, so expired tokens are rejected by Cloud Shell. Tokens are never automatically
refreshed or replaced with another credential. Plaintext strings are not accepted.
The token must be valid for public Azure Resource Manager
and come from a client permitted by Cloud Shell; `-AccessToken` does not bypass the service's client restrictions.

## Limitations

- The session runs in a separate `pwsh -s` process started from one Cloud Shell terminal, so `$PID` is that
  process and the Cloud Shell profile isn't loaded. Closing the session ends the server and returns the terminal to
  its prompt. If the WebSocket drops, the session becomes Broken. It does not reconnect, and
  commands are never replayed.
- Each session acquires its credential, requests the Cloud Shell console, and makes a new terminal-creation call.
  The module does not cache consoles or access tokens between sessions. Azure may return the same running console,
  but each session uses its own terminal. Cloud Shell's own idle timeout (about 20 minutes) still applies.
- `Remove-PSSession` stops the remote `pwsh -s` and closes the WebSocket. `Disconnect-PSSession` isn't supported:
  PowerShell only allows disconnect for WSMan sessions, so it reports an error and the session stays open and usable.
- Sovereign clouds aren't supported.
- Starting a session takes a few seconds with a warm Cloud Shell. A cold Cloud Shell container takes longer. The cmdlet waits for the bash prompt
  before it types anything, and it answers the terminal's cursor-position queries (`ESC[6n`) the way a terminal
  emulator would.

## Validation

The module was tested live against public Azure Cloud Shell (PowerShell 7.6.9, Linux), with Windows as the local
client. These all worked:
- `$PID` (the `pwsh -s` process) and `$using:` with Unicode text
- 20,000 lines of output (about 4 s) and a 300 KB argument (under 1 s)
- remote errors, native commands, and the Cloud Shell Az context
- separate terminals for multiple sessions, `Remove-PSSession`, and the `Disconnect-PSSession` error

The automated end-to-end test emulates the terminal and the launcher, and runs PSRP against a real `pwsh -s` on
Windows. The real `bash`/`stty` launcher is only exercised in Cloud Shell. Linux and macOS clients haven't been tested.

## Build and test

```powershell
./build.ps1             # publish to Artifacts\publish\JAz.CloudShell\release and run all tests
./build.ps1 -SkipTests
```

Build and intermediate outputs use .NET's centralized artifacts layout under `Artifacts\bin` and `Artifacts\obj`.
NuGet package versions are managed centrally in `Directory.Packages.props`.
Building requires Az.Accounts 5.3.3 or later installed for the native context and token reference assemblies.
MSBuild discovers the installed module automatically; use `-p:AzAccountsModulePath=<module-directory>` to override it.
Published modules go to `Artifacts\publish\JAz.CloudShell\<configuration>`, and the build script writes test results
to `Artifacts\TestResults\<configuration>`. Debug and Release outputs are kept separate.

The tests need `pwsh` on `PATH`. Profile integration tests also need Az.Accounts installed and are skipped if it is
unavailable; they mock token acquisition and HTTP requests and never sign in or contact Azure.
The end-to-end test starts a loopback WebSocket "terminal" backed by a real `pwsh`
server-mode process and runs `New-JAzCloudShellSession` and `Invoke-Command` against it.
