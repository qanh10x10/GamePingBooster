# Real local named pipe; service/ACL/file mutations are mocked. Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
$text = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'setup-service.ps1') -Raw
$acl = '& "$env:SystemRoot\System32\icacls.exe" $data /inheritance:r /grant:r ''*S-1-5-18:(OI)(CI)F'' ''*S-1-5-32-544:(OI)(CI)F'''
if (-not $text.Contains($acl)) { throw 'ACL mock seam changed; refusing execution.' }
$prelude = @'
Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management,Microsoft.PowerShell.Security,CimCmdlets
function Get-CimInstance {
    param($ClassName,$Filter)
    if ($Filter -cne "Name='HollowPingBooster'") { throw 'Unexpected service target' }
    if ($global:created) { return @{PathName=('"' + (Join-Path $env:ProgramFiles 'Hollow Ping Booster\gpb-service.exe') + '"').ToLowerInvariant();StartName='LocalSystem';StartMode='Auto'} }
}
function Get-Content { return '{"f01":"a","f02":"a","f03":"a","f04":"a","f05":"a","f06":"a","f07":"a","f08":"a","f09":"a","f10":"a","f11":"a"}' }
function Get-FileHash { return @{Hash='a'} }
function Get-AuthenticodeSignature { return @{Status='Valid';SignerCertificate=@{Subject='O=WireGuard LLC'}} }
function Test-Path { return $true }
function Get-Item { return @{Attributes=0} }
function Get-ChildItem { return @() }
function New-Item { throw 'Unexpected file mutation' }
function Invoke-CimMethod {
    param($ClassName,$MethodName,$Arguments,$InputObject)
    if ($MethodName -eq 'Create') {
        if ($Arguments.Name -cne 'HollowPingBooster' -or $Arguments.StartName -ne 'LocalSystem' -or $Arguments.StartMode -ne 'Automatic') { throw 'Unexpected registration' }
        $global:created=$true
    } elseif ($MethodName -eq 'Delete') { $global:created=$false; [Console]::WriteLine('MOCK:delete') }
    else { throw 'Unexpected CIM method' }
    return @{ReturnValue=0}
}
function Start-Service { param($Name); if ($Name -cne 'HollowPingBooster') { throw 'Unexpected start' }; $global:running=$true }
function Stop-Service { param($Name); if ($Name -cne 'HollowPingBooster') { throw 'Unexpected stop' }; $global:running=$false; [Console]::WriteLine('MOCK:stop') }
function Get-Service {
    param($Name)
    if ($Name -cne 'HollowPingBooster') { throw 'Unexpected service read' }
    $status = if ($global:running) { 'Running' } else { 'Stopped' }
    return (New-Object PSObject -Property @{Status=$status} | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value {} -PassThru | Add-Member -MemberType ScriptMethod -Name Dispose -Value {} -PassThru)
}
'@
foreach ($case in @('valid','wrong-state','silent')) {
    $name = 'HollowSetupTest' + [guid]::NewGuid().ToString('N')
    $path = Join-Path $env:TMPDIR ($name + '.ps1'); $out=$path+'.out'; $err=$path+'.err'
    $writer=$null; $process=$null
    $server = New-Object IO.Pipes.NamedPipeServerStream($name,[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $wait = $server.WaitForConnectionAsync()
        $probe = $text.Replace($acl,'$global:LASTEXITCODE=0').Replace("'.', 'HollowPingBooster',","'.', '$name',").Replace("`$ErrorActionPreference = 'Stop'","`$ErrorActionPreference = 'Stop'`n"+$prelude)
        [IO.File]::WriteAllText($path,$probe,(New-Object Text.UTF8Encoding($false)))
        $args = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$path+'" -InstallDir "'+(Join-Path $env:ProgramFiles 'Hollow Ping Booster')+'"'
        $process = Start-Process "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $args -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err
        if (-not $wait.Wait(15000)) { throw ('No pipe connection: '+(Get-Content $out -Raw)+(Get-Content $err -Raw)) }
        if ($case -ne 'silent') {
            $writer = New-Object IO.StreamWriter($server,(New-Object Text.UTF8Encoding($false)))
            $writer.AutoFlush=$true
            $state = if ($case -eq 'valid') { 0 } else { 2 }
            $writer.WriteLine('{"v":2,"state":'+$state+',"fullTunnel":true}')
        }
        if (-not $process.WaitForExit(15000)) { throw "FAIL: $case IPC read exceeded its deadline" }
        $result = Get-Content $out -Raw
        if ((Get-Item $err).Length -gt 0) { throw (Get-Content $err -Raw) }
        if ($case -eq 'valid') {
            if ($result -notmatch 'installed and IPC verified' -or $result -match 'ERROR|MOCK:') { throw "Valid IPC rejected: $result" }
        } elseif ($result -notmatch 'ERROR \[check-ipc\]' -or $result -notmatch 'MOCK:stop' -or $result -notmatch 'MOCK:delete') { throw "Failed IPC not rolled back: $result" }
        Write-Output "PASS: $case IPC (real pipe; mocked service)"
    } finally {
        if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        if ($writer) { $writer.Dispose() }
        $server.Dispose()
        foreach ($f in @($path,$out,$err)) { Remove-Item -LiteralPath $f -ErrorAction SilentlyContinue }
    }
}
