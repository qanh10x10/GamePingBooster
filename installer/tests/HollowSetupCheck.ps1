# Run with Windows PowerShell 5.1; no service, registry or network writes.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$helper = Join-Path $root 'setup-service.ps1'
$text = Get-Content $helper -Raw
# Win32_Service.Create exposes these numeric fields as CIM UInt8; PowerShell 5.1 spells that CLR type [byte].
if ($text -notmatch 'ServiceType\s*=\s*\[byte\]16' -or $text -notmatch 'ErrorControl\s*=\s*\[byte\]1') {
    throw 'Service registration must pass [byte] values for the CIM UInt8 fields.'
}
# Reproduce the registration-failure boundary without executing any OS mutation.
$prelude = @'
Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management,Microsoft.PowerShell.Security,CimCmdlets
function Get-CimInstance { return $null }
function Get-Content { return '{"file01":"a","file02":"a","file03":"a","file04":"a","file05":"a","file06":"a","file07":"a","file08":"a","file09":"a","file10":"a","file11":"a"}' }
function Get-FileHash { return @{Hash='a'} }
function Get-AuthenticodeSignature { return @{Status='Valid';SignerCertificate=@{Subject='O=WireGuard LLC'}} }
function Test-Path { return $true }
function Get-Item { return @{Attributes=0} }
function Get-ChildItem { return @() }
function New-Item {}
function Invoke-CimMethod { return @{ReturnValue=5} }
function Start-Service { throw 'Unexpected service start' }
'@
$probe = $text.Replace('& "$env:SystemRoot\System32\icacls.exe" $data /inheritance:r /grant:r ''*S-1-5-18:(OI)(CI)F'' ''*S-1-5-32-544:(OI)(CI)F''', '$global:LASTEXITCODE=0')
$temp = Join-Path $env:TMPDIR ('hollow-setup-probe-' + [guid]::NewGuid().ToString('N') + '.ps1')
try {
    [IO.File]::WriteAllText($temp, ($probe.Replace("`$ErrorActionPreference = 'Stop'", "`$ErrorActionPreference = 'Stop'`n" + $prelude)), (New-Object Text.UTF8Encoding($false)))
    $output = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $temp -InstallDir (Join-Path $env:ProgramFiles 'Hollow Ping Booster') 2>&1
    if ($LASTEXITCODE -ne 1) { throw "Expected failed registration, got exit $LASTEXITCODE" }
    if (($output -join "`n") -notmatch 'ERROR \[register-service\].*5') { throw "Registration failure lost its stage/cause: $output" }
    $iss = Get-Content (Join-Path $root 'HollowSetup.iss') -Raw
    if ($iss -notmatch 'OnHelperLog' -or $iss -notmatch 'LastHelperError') { throw 'Setup discards helper error instead of displaying its cause.' }
    Write-Output 'PASS: registration failure preserves stage and code; installer displays helper cause.'
} finally { Remove-Item $temp -ErrorAction SilentlyContinue }

# DeleteService remains pending while ServiceController owns its open handle.
$prelude = @'
Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management,CimCmdlets
function Test-Path { return $false }
function Get-CimInstance {
    if (-not $global:removed) { return @{PathName=('"' + (Join-Path $env:ProgramFiles 'Hollow Ping Booster\gpb-service.exe') + '"').ToLowerInvariant();StartName='LocalSystem'} }
}
function Get-Service {
    return (New-Object PSObject -Property @{Status='Stopped'} |
        Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value {} -PassThru |
        Add-Member -MemberType ScriptMethod -Name Dispose -Value { $global:disposed=$true } -PassThru)
}
function Stop-Service { throw 'Unexpected service stop' }
function Invoke-CimMethod {
    if (-not $global:disposed) { throw 'ServiceController handle remains open during DeleteService' }
    $global:removed=$true
    return @{ReturnValue=0}
}
'@
try {
    [IO.File]::WriteAllText($temp, $text.Replace("`$ErrorActionPreference = 'Stop'", "`$ErrorActionPreference = 'Stop'`n" + $prelude), (New-Object Text.UTF8Encoding($false)))
    $output = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $temp -InstallDir (Join-Path $env:ProgramFiles 'Hollow Ping Booster') -Uninstall 2>&1
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch 'service removed') { throw "Uninstall failed to release controller before deletion: $output" }
    Write-Output 'PASS: uninstall releases its service handle before DeleteService.'
} finally { Remove-Item $temp -ErrorAction SilentlyContinue }
