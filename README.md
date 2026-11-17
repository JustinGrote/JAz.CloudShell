# JAz.CloudShell

A custom PowerShell remoting transport for **Azure Cloud Shell**. `New-JAzCloudShellSession` returns a normal
`PSSession`, so you can use the usual remoting commands with it:

```powershell
Import-Module ./out/JAz.CloudShell
$cs = New-JAzCloudShellSession -Verbose
Invoke-Command -Session $cs { $PID; $PSVersionTable.PSVersion; Get-AzContext }
Enter-PSSession $cs
Remove-PSSession $cs
```

## How it works

1. **Auth.** On Windows, the default authentication path first checks Web Account Manager (WAM) silently for a token
   for the Windows default account, honoring `-TenantId` when supplied. It never opens a sign-in or account-selection
   dialog. If WAM is unavailable or needs interaction, it falls back to `DefaultAzureCredential`'s noninteractive
   chain: environment variables, workload identity, managed identity, Visual Studio, Azure CLI, Azure PowerShell,
   and so on. Other operating systems use that chain directly. `-Verbose` reports WAM selection or fallback.
   Use `-NoWAM` to skip the WAM check and go straight to DefaultAzureCredential. Its console cache is separate
   from WAM-enabled sessions, so it cannot reuse a token previously obtained through WAM.
   With `-Context`, the cmdlet instead uses `Get-AzAccessToken -DefaultProfile` from Az.Accounts for each supplied
   `IAzureContextContainer`. It never switches the active Az context or falls back to another credential.
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
- A Cloud Shell account that is already onboarded in the public Azure cloud. Open https://shell.azure.com once to set it
  up. The module never onboards an account or changes your Cloud Shell settings.

## Parameters

| Parameter | Description |
| --- | --- |
| `-Name` | PSSession name. |
| `-TenantId` | Tenant for `DefaultAzureCredential`. |
| `-NoWAM` | Skip the silent Windows WAM check. Available only with default authentication, not `-Context` or `-AccessToken`. |
| `-Context` | One or more Az.Accounts `IAzureContextContainer` profiles. Accepts pipeline input and the same profile conversions as `Get-AzAccessToken -DefaultProfile`. Cannot be combined with `-TenantId`. |
| `-AccessToken` | An Az.Accounts `PSSecureAccessToken` object or a `SecureString` containing an ARM access token. Uses a separate parameter set; cannot be combined with `-Context` or `-TenantId`. |
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
applies separately to each profile. Az.Accounts is only needed when `-Context` is supplied.

Cloud Shell can reject Azure PowerShell-issued tokens with `ClientNotAllowed`. This service restriction is reported
unchanged; supplying a profile does not bypass it or silently substitute an Azure CLI identity. Omit `-Context` to use
the existing DefaultAzureCredential flow instead.

### Supplied access tokens

```powershell
$token = Get-AzAccessToken -ResourceTypeName Arm
$cs = New-JAzCloudShellSession -AccessToken $token

# Alternatively, supply its SecureString directly:
$cs = New-JAzCloudShellSession -AccessToken $token.Token
```

The object form reads the secure string from `Token` and honors `ExpiresOn`; expired tokens are rejected locally.
Provide the full token object for expiry-aware console reuse. A bare `SecureString` has no expiry metadata.
Tokens are never automatically refreshed or replaced with another credential. Plaintext strings are not accepted.
Supplying a `SecureString` does not require Az.Accounts. The token must be valid for public Azure Resource Manager
and come from a client permitted by Cloud Shell; `-AccessToken` does not bypass the service's client restrictions.

## Limitations

- The session runs in a separate `pwsh -s` process started from one Cloud Shell terminal, so `$PID` is that
  process and the Cloud Shell profile isn't loaded. Closing the session ends the server and returns the terminal to
  its prompt. If the WebSocket drops, the session becomes Broken. It does not reconnect, and
  commands are never replayed.
- Each session uses its own Cloud Shell terminal. Within one PowerShell process, later context-based sessions for the same account and tenant
  reuse the Cloud Shell console and access token (while the token has at least 5 minutes left) and only create a new
  terminal. If the cached console is gone, the cmdlet provisions a fresh one automatically. Cloud Shell's own idle
  timeout (about 20 minutes) still applies.
  DefaultAzureCredential sessions retain their separate, per-tenant console cache.
- console reuse for a second session, `Remove-PSSession`, and the `Disconnect-PSSession` error stops the remote `pwsh -s` and closes the WebSocket. `Disconnect-PSSession` isn't supported:
  PowerShell only allows disconnect for WSMan sessions, so it reports an error and the session stays open and usable.
- Sovereign clouds aren't supported.
- Starting a session takes a few seconds with a warm Cloud Shell (about 7 s for the first session and 2 s for a
  session that reuses the console). A cold Cloud Shell container takes longer. The cmdlet waits for the bash prompt
  before it types anything, and it answers the terminal's cursor-position queries (`ESC[6n`) the way a terminal
  emulator would.

## Validation

The module was tested live against public Azure Cloud Shell (PowerShell 7.6.9, Linux), with Windows as the local
client. These all worked:
- `$PID` (the `pwsh -s` process) and `$using:` with Unicode text
- 20,000 lines of output (about 4 s) and a 300 KB argument (under 1 s)
- remote errors, native commands, and the Cloud Shell Az context
- console reuse for a second session, `Remove-PSSession`, and the `Disconnect-PSSession` error

The automated end-to-end test emulates the terminal and the launcher, and runs PSRP against a real `pwsh -s` on
Windows. The real `bash`/`stty` launcher is only exercised in Cloud Shell. Linux and macOS clients haven't been tested.

## Build and test

```powershell
./build.ps1             # publish to out/JAz.CloudShell and run all tests
./build.ps1 -SkipTests
```

The tests need `pwsh` on `PATH`. Profile integration tests also need Az.Accounts installed and are skipped if it is
unavailable; they mock token acquisition and HTTP requests and never sign in or contact Azure.
The end-to-end test starts a loopback WebSocket "terminal" backed by a real `pwsh`
server-mode process and runs `New-JAzCloudShellSession` and `Invoke-Command` against it.
