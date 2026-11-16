# Temporary bridge helper launched inside Azure Cloud Shell by JAz.CloudShell.
# Relays framed, base64-encoded bytes between this process's terminal (stdin/stdout)
# and the PowerShell remoting named pipe of the parent PowerShell process __PID__.
# Frames: ~JZ<nonce>|<kind>|<base64>~   remote->local kinds: R(eady) D(ata) E(rror) C(lose)
#                                       local->remote kinds: d(ata) c(lose)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$n = '__NONCE__'
$t = __PID__
$fw = 'param($o, $n, $k, $b, $c) $s = "~JZ$n|$k|" + [Convert]::ToBase64String($b, 0, $c) + "~`n"; $x = [Text.Encoding]::ASCII.GetBytes($s); [Threading.Monitor]::Enter($o); try { $o.Write($x, 0, $x.Length); $o.Flush() } finally { [Threading.Monitor]::Exit($o) }'
$W = [scriptblock]::Create($fw)
if ($IsWindows) {
    $i = [Console]::OpenStandardInput()
    $o = [Console]::OpenStandardOutput()
} else {
    # Use the raw file descriptors so .NET console line editing/echo never touches the stream.
    $i = [IO.FileStream]::new([Microsoft.Win32.SafeHandles.SafeFileHandle]::new([IntPtr]0, $false), [IO.FileAccess]::Read, 1)
    $o = [IO.FileStream]::new([Microsoft.Win32.SafeHandles.SafeFileHandle]::new([IntPtr]1, $false), [IO.FileAccess]::Write, 1)
}
function Send-Frame([string]$k, [byte[]]$b) { & $W $o $n $k $b $b.Length }
$tty = $false
$p = $null
$ps = $null
try {
    if (-not $IsWindows -and -not [Console]::IsInputRedirected) {
        $tty = $true
        & stty raw -echo 2>$null
        if ($LASTEXITCODE) { throw 'stty could not configure the terminal.' }
        $mode = (& stty -a) -join ' '
        if ($mode -match '(^|[\s;])echo([\s;]|$)') { throw 'Unable to disable terminal echo.' }
    }
    $null = Get-Process -Id $t
    if ($IsWindows) {
        $pfx = ''
        $names = [IO.Directory]::GetFiles('\\.\pipe\') | ForEach-Object { [IO.Path]::GetFileName($_) }
    } else {
        $pfx = 'CoreFxPipe_'
        $names = [IO.Directory]::GetFiles([IO.Path]::GetTempPath(), 'CoreFxPipe_PSHost.*') | ForEach-Object { [IO.Path]::GetFileName($_) }
    }
    $m = @($names | Where-Object { $_.StartsWith($pfx + 'PSHost.') -and $_.Split('.')[2] -eq "$t" })
    if ($m.Count -ne 1) { throw "Expected one PowerShell remoting named pipe for process $t but found $($m.Count)." }
    $p = [IO.Pipes.NamedPipeClientStream]::new('.', $m[0].Substring($pfx.Length), [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $p.Connect(15000)

    $ps = [powershell]::Create()
    $null = $ps.AddScript('param($p, $o, $n, $fw) $W = [scriptblock]::Create($fw); $b = [byte[]]::new(3072); try { while (($c = $p.Read($b, 0, $b.Length)) -gt 0) { & $W $o $n "D" $b $c } } catch { }; & $W $o $n "C" $b 0').
        AddArgument($p).AddArgument($o).AddArgument($n).AddArgument($fw)
    $h = $ps.BeginInvoke()
    Send-Frame 'R' ([Text.Encoding]::ASCII.GetBytes('1'))

    $r = [IO.StreamReader]::new($i, [Text.Encoding]::ASCII, $false, 65536)
    $pre = "~JZ$n|"
    while ($null -ne ($l = $r.ReadLine())) {
        $x = $l.IndexOf($pre)
        if ($x -lt 0) { continue }
        $f = $l.Substring($x + $pre.Length)
        $e = $f.IndexOf('~')
        if ($e -lt 2 -or $f[1] -ne '|') { throw 'Malformed bridge frame received.' }
        $k = [string]$f[0]
        if ($k -ceq 'c') { break }
        if ($k -ceq 'd') {
            $bb = [Convert]::FromBase64String($f.Substring(2, $e - 2))
            $p.Write($bb, 0, $bb.Length)
            $p.Flush()
        }
    }
} catch {
    Send-Frame 'E' ([Text.Encoding]::UTF8.GetBytes($_.Exception.Message))
} finally {
    if ($p) { $p.Dispose() }
    if ($ps) { $null = $h.AsyncWaitHandle.WaitOne(2000); $ps.Dispose() } else { Send-Frame 'C' ([byte[]]@()) }
    if ($tty) { & stty sane 2>$null }
}
