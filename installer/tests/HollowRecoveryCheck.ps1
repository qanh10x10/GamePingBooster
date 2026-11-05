# Regression check for Hollow installer safety, rollback, and partial-copy recovery.
# Tests run entirely against isolated disposable scratch fixtures with mocked OS service/ACL writes.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$helperSource = Join-Path $repo 'installer\setup-service.ps1'
$helperText = Get-Content $helperSource -Raw

$scratch = if ($env:TMPDIR -and (Test-Path $env:TMPDIR) -and $env:TMPDIR -match 'hermes\\cache\\scratch') { $env:TMPDIR } else { 'C:\Users\PC\AppData\Local\hermes\cache\scratch' }
if (-not (Test-Path $scratch)) { New-Item -ItemType Directory -Path $scratch | Out-Null }
$testRoot = Join-Path $scratch ('hollow-recovery-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

try {
    # Helper to create isolated test script
    function New-ProbeScript([string]$scriptText, [string]$installDir, [string]$dataDir) {
        $replaced = $scriptText.Replace("`$expected = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Hollow Ping Booster'", "`$expected = '$installDir'")
        $replaced = $replaced.Replace("`$data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'HollowPingBooster'", "`$data = '$dataDir'")
        $path = Join-Path $testRoot ('probe-' + [guid]::NewGuid().ToString('N') + '.ps1')
        [IO.File]::WriteAllText($path, $replaced, (New-Object Text.UTF8Encoding($false)))
        return $path
    }

    # -------------------------------------------------------------
    # BUG 1: Reparse Point Checks
    # -------------------------------------------------------------
    Write-Output "--- Testing Bug 1: Reparse Point Rejection ---"
    $bug1Dir = Join-Path $testRoot 'bug1'
    $install1 = Join-Path $bug1Dir 'install'
    $data1 = Join-Path $bug1Dir 'data'
    $target1 = Join-Path $bug1Dir 'target'
    $logsJunction = Join-Path $data1 'logs'
    New-Item -ItemType Directory -Path $install1, $data1, $target1 | Out-Null
    New-Item -ItemType Junction -Path $logsJunction -Target $target1 | Out-Null

    $prelude1 = @'
Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management,CimCmdlets
function Get-CimInstance { return $null }
'@
    $probe1 = New-ProbeScript ($helperText.Replace("`$ErrorActionPreference = 'Stop'", "`$ErrorActionPreference = 'Stop'`n" + $prelude1)) $install1 $data1
    $out1 = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probe1 -InstallDir $install1 -CheckOnly 2>&1
    $exit1 = $LASTEXITCODE

    if ($exit1 -ne 1 -or ($out1 -join "`n") -notmatch 'Redirected.*data') {
        throw "FAIL Bug 1a: Nested junction in ProgramData must be rejected before icacls (got exit $($exit1): $($out1))"
    }
    Write-Output "PASS Bug 1a: ProgramData nested junction rejected."

    # Reparse point in payload directory
    $payloadJunction = Join-Path $install1 'subjunction'
    New-Item -ItemType Junction -Path $payloadJunction -Target $target1 | Out-Null
    [IO.Directory]::Delete($logsJunction) # clear data junction so we isolate payload junction test

    $out1b = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probe1 -InstallDir $install1 -CheckOnly 2>&1
    $exit1b = $LASTEXITCODE
    if ($exit1b -ne 1 -or ($out1b -join "`n") -notmatch 'Redirected.*payload') {
        throw "FAIL Bug 1b: Reparse point in payload must be rejected before hashes/traversal (got exit $($exit1b): $($out1b))"
    }
    [IO.Directory]::Delete($payloadJunction)
    Write-Output "PASS Bug 1b: Payload reparse point rejected."

    # -------------------------------------------------------------
    # BUG 2: Rollback of Newly-Created Service
    # -------------------------------------------------------------
    Write-Output "--- Testing Bug 2: Rollback After Failed Stop ---"
    $bug2Dir = Join-Path $testRoot 'bug2'
    $install2 = Join-Path $bug2Dir 'install'
    $data2 = Join-Path $bug2Dir 'data'
    New-Item -ItemType Directory -Path $install2, $data2 | Out-Null

    $acl = '& "$env:SystemRoot\System32\icacls.exe" $data /inheritance:r /grant:r ''*S-1-5-18:(OI)(CI)F'' ''*S-1-5-32-544:(OI)(CI)F'''
    $prelude2 = @'
Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management,Microsoft.PowerShell.Security,CimCmdlets
function Get-CimInstance {
    param($ClassName, $Filter)
    if ($global:created) {
        return @{
            PathName = ('"' + (Join-Path $global:mockInstall 'gpb-service.exe') + '"');
            StartName = 'LocalSystem'
        }
    }
    return $null
}
function Get-Content { return '{"f01":"a","f02":"a","f03":"a","f04":"a","f05":"a","f06":"a","f07":"a","f08":"a","f09":"a","f10":"a","f11":"a"}' }
function Get-FileHash { return @{Hash='a'} }
function Get-AuthenticodeSignature { return @{Status='Valid';SignerCertificate=@{Subject='O=WireGuard LLC'}} }
function Test-Path { return $true }
function Get-Item { return @{Attributes=0} }
function Get-ChildItem { return @() }
function New-Item {}
function Invoke-CimMethod {
    param($ClassName, $MethodName, $Arguments, $InputObject)
    if ($MethodName -eq 'Create') { $global:created = $true; return @{ReturnValue=0} }
    if ($MethodName -eq 'Delete') {
        [Console]::WriteLine('MOCK:delete')
        $global:created = $false
        return @{ReturnValue=0}
    }
    throw "Unexpected CIM method $MethodName"
}
function Start-Service { throw 'Simulated failed start' }
function Stop-Service { throw 'Simulated failed stop' }
function Get-Service {
    return (New-Object PSObject -Property @{Status='Running'} |
        Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value {} -PassThru |
        Add-Member -MemberType ScriptMethod -Name Dispose -Value { [Console]::WriteLine('MOCK:dispose') } -PassThru)
}
'@
    $text2 = $helperText.Replace($acl, '$global:LASTEXITCODE=0').Replace("`$ErrorActionPreference = 'Stop'", "`$ErrorActionPreference = 'Stop'`n`$global:mockInstall='$install2'`n" + $prelude2)
    $probe2 = New-ProbeScript $text2 $install2 $data2
    $out2 = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probe2 -InstallDir $install2 2>&1
    $exit2 = $LASTEXITCODE
    $out2Str = $out2 -join "`n"

    if ($exit2 -ne 1) { throw "FAIL Bug 2: Expected exit 1, got $exit2" }
    if ($out2Str -notmatch 'MOCK:delete') { throw "FAIL Bug 2: Delete was not attempted after failed stop: $out2Str" }
    if ($out2Str -notmatch 'MOCK:dispose') { throw "FAIL Bug 2: ServiceController was not disposed during rollback: $out2Str" }
    if ($out2Str -notmatch 'Simulated failed start') { throw "FAIL Bug 2: Original failure was lost: $out2Str" }
    if ($out2Str -notmatch 'Simulated failed stop') { throw "FAIL Bug 2: Stop failure was not reported: $out2Str" }
    Write-Output "PASS Bug 2: Rollback attempts Delete, disposes controller, preserves original and stop errors."

    # Ownership check during rollback
    $prelude2Ownership = @'
Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management,Microsoft.PowerShell.Security,CimCmdlets
function Get-CimInstance {
    param($ClassName, $Filter)
    if ($global:created) {
        # Ownership hijacked!
        return @{
            PathName = '"C:\Malicious\evil.exe"';
            StartName = 'LocalSystem'
        }
    }
    return $null
}
function Get-Content { return '{"f01":"a","f02":"a","f03":"a","f04":"a","f05":"a","f06":"a","f07":"a","f08":"a","f09":"a","f10":"a","f11":"a"}' }
function Get-FileHash { return @{Hash='a'} }
function Get-AuthenticodeSignature { return @{Status='Valid';SignerCertificate=@{Subject='O=WireGuard LLC'}} }
function Test-Path { return $true }
function Get-Item { return @{Attributes=0} }
function Get-ChildItem { return @() }
function New-Item {}
function Invoke-CimMethod {
    param($ClassName, $MethodName, $Arguments, $InputObject)
    if ($MethodName -eq 'Create') { $global:created = $true; return @{ReturnValue=0} }
    if ($MethodName -eq 'Delete') { throw 'MOCK:delete called on non-owned service!' }
    throw "Unexpected CIM method $MethodName"
}
function Start-Service { throw 'Simulated failed start' }
function Stop-Service { throw 'Simulated failed stop' }
function Get-Service {
    return (New-Object PSObject -Property @{Status='Running'} |
        Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value {} -PassThru |
        Add-Member -MemberType ScriptMethod -Name Dispose -Value {} -PassThru)
}
'@
    $text2b = $helperText.Replace($acl, '$global:LASTEXITCODE=0').Replace("`$ErrorActionPreference = 'Stop'", "`$ErrorActionPreference = 'Stop'`n`$global:mockInstall='$install2'`n" + $prelude2Ownership)
    $probe2b = New-ProbeScript $text2b $install2 $data2
    $out2b = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probe2b -InstallDir $install2 2>&1
    $out2bStr = $out2b -join "`n"
    if ($out2bStr -notmatch 'service ownership changed; rollback refused') {
        throw "FAIL Bug 2: Deletion must be refused if service ownership changed: $out2bStr"
    }
    Write-Output "PASS Bug 2: Service ownership verified before rollback deletion."

    # Pending deletion state reporting
    $prelude2Pending = @'
Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management,Microsoft.PowerShell.Security,CimCmdlets
function Get-CimInstance {
    param($ClassName, $Filter)
    if ($global:created) {
        # Service still returned after Delete (pending deletion)
        return @{
            PathName = ('"' + (Join-Path $global:mockInstall 'gpb-service.exe') + '"');
            StartName = 'LocalSystem'
        }
    }
    return $null
}
function Get-Content { return '{"f01":"a","f02":"a","f03":"a","f04":"a","f05":"a","f06":"a","f07":"a","f08":"a","f09":"a","f10":"a","f11":"a"}' }
function Get-FileHash { return @{Hash='a'} }
function Get-AuthenticodeSignature { return @{Status='Valid';SignerCertificate=@{Subject='O=WireGuard LLC'}} }
function Test-Path { return $true }
function Get-Item { return @{Attributes=0} }
function Get-ChildItem { return @() }
function New-Item {}
function Invoke-CimMethod {
    param($ClassName, $MethodName, $Arguments, $InputObject)
    if ($MethodName -eq 'Create') { $global:created = $true; return @{ReturnValue=0} }
    if ($MethodName -eq 'Delete') { [Console]::WriteLine('MOCK:delete'); return @{ReturnValue=0} }
    throw "Unexpected CIM method $MethodName"
}
function Start-Service { throw 'Simulated failed start' }
function Stop-Service { return }
function Get-Service {
    return (New-Object PSObject -Property @{Status='Stopped'} |
        Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value {} -PassThru |
        Add-Member -MemberType ScriptMethod -Name Dispose -Value {} -PassThru)
}
'@
    $text2c = $helperText.Replace($acl, '$global:LASTEXITCODE=0').Replace("`$ErrorActionPreference = 'Stop'", "`$ErrorActionPreference = 'Stop'`n`$global:mockInstall='$install2'`n" + $prelude2Pending)
    $probe2c = New-ProbeScript $text2c $install2 $data2
    $out2c = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probe2c -InstallDir $install2 2>&1
    $out2cStr = $out2c -join "`n"
    if ($out2cStr -notmatch 'service is pending deletion') {
        throw "FAIL Bug 2: Pending deletion must be reported honestly: $out2cStr"
    }
    Write-Output "PASS Bug 2: Pending deletion reported honestly."

    # -------------------------------------------------------------
    # BUG 3: Partial Copy Recovery via Bundled Trusted Manifest
    # -------------------------------------------------------------
    Write-Output "--- Testing Bug 3: Partial Copy Recovery & Subset Validation ---"
    $bug3Dir = Join-Path $testRoot 'bug3'
    $install3 = Join-Path $bug3Dir 'install'
    $data3 = Join-Path $bug3Dir 'data'
    New-Item -ItemType Directory -Path $install3, $data3 | Out-Null

    # Generate dummy trusted manifest with 15 files
    $manifestObj = [ordered]@{}
    $trustedManifestFile = Join-Path $bug3Dir 'trusted-manifest.json'
    for ($i = 1; $i -le 15; $i++) {
        $fname = "file$('{0:D2}' -f $i).dll"
        $fcontent = "content-of-file-$i"
        $sha = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($fcontent))) -Algorithm SHA256).Hash
        $manifestObj[$fname] = $sha
    }
    $manifestJson = $manifestObj | ConvertTo-Json
    [IO.File]::WriteAllText($trustedManifestFile, $manifestJson, (New-Object Text.UTF8Encoding($false)))

    $prelude3 = @'
Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management,CimCmdlets
function Get-CimInstance { return $null }
'@
    $probe3 = New-ProbeScript ($helperText.Replace("`$ErrorActionPreference = 'Stop'", "`$ErrorActionPreference = 'Stop'`n" + $prelude3)) $install3 $data3

    # Case 3a: Only 3 files exist on disk (no BUILD-INFO, no SHA256SUMS.json on disk)
    for ($i = 1; $i -le 3; $i++) {
        $fname = "file$('{0:D2}' -f $i).dll"
        [IO.File]::WriteAllText((Join-Path $install3 $fname), "content-of-file-$i", (New-Object Text.UTF8Encoding($false)))
    }
    # Also add narrow uninstaller metadata
    [IO.File]::WriteAllText((Join-Path $install3 'unins000.exe'), "dummy-uninstaller", (New-Object Text.UTF8Encoding($false)))

    # Without PayloadManifest, it must fail closed
    $out3Fail = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probe3 -InstallDir $install3 -CheckOnly 2>&1
    if ($LASTEXITCODE -ne 1) {
        throw "FAIL Bug 3a: Partial copy without trusted manifest or BUILD-INFO must fail closed"
    }

    # WITH PayloadManifest, valid subset must PASS
    $out3Pass = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probe3 -InstallDir $install3 -CheckOnly -PayloadManifest $trustedManifestFile 2>&1
    $exit3Pass = $LASTEXITCODE
    if ($exit3Pass -ne 0 -or ($out3Pass -join "`n") -notmatch 'Install checks passed') {
        throw "FAIL Bug 3b: Valid subset of trusted manifest must succeed (got exit $($exit3Pass): $($out3Pass))"
    }
    Write-Output "PASS Bug 3: Valid partial copy subset verified and accepted."

    # Case 3c: Modified file in partial copy must be REJECTED
    [IO.File]::WriteAllText((Join-Path $install3 'file02.dll'), "corrupted-content", (New-Object Text.UTF8Encoding($false)))
    $out3Mod = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probe3 -InstallDir $install3 -CheckOnly -PayloadManifest $trustedManifestFile 2>&1
    if ($LASTEXITCODE -ne 1 -or ($out3Mod -join "`n") -notmatch 'Previous payload was modified: file02.dll') {
        throw "FAIL Bug 3c: Modified file must be rejected (got $out3Mod)"
    }
    Write-Output "PASS Bug 3: Modified file in partial copy rejected."

    # Restore file02.dll, add unknown file
    [IO.File]::WriteAllText((Join-Path $install3 'file02.dll'), "content-of-file-2", (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $install3 'unknown-evil.dll'), "untrusted", (New-Object Text.UTF8Encoding($false)))
    $out3Unk = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probe3 -InstallDir $install3 -CheckOnly -PayloadManifest $trustedManifestFile 2>&1
    if ($LASTEXITCODE -ne 1 -or ($out3Unk -join "`n") -notmatch 'Unknown file in previous payload: unknown-evil.dll') {
        throw "FAIL Bug 3d: Unknown file must be rejected (got $out3Unk)"
    }
    Write-Output "PASS Bug 3: Unknown file rejected."

    Write-Output "ALL SAFETY AND RECOVERY REGRESSION CHECKS PASSED!"
} finally {
    if (Test-Path $testRoot) {
        # Ensure any junctions are unlinked before recursive delete
        Get-ChildItem $testRoot -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | ForEach-Object {
            [IO.Directory]::Delete($_.FullName)
        }
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
