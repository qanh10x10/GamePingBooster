param(
    [Parameter(Mandatory=$true)][string]$InstallDir,
    [switch]$CheckOnly,
    [switch]$Uninstall,
    [string]$PayloadManifest
)
$ErrorActionPreference = 'Stop'
$created = $false
$startAttempted = $false
$step = 'preflight'

function Get-SafeChildItems([string]$rootPath, [string]$label) {
    if (-not (Test-Path -LiteralPath $rootPath)) { return @() }
    $rootItem = Get-Item -LiteralPath $rootPath -Force
    if ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Redirected $label directory is not supported."
    }
    $files = [System.Collections.Generic.List[System.IO.FileSystemInfo]]::new()
    $queue = [System.Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($rootPath)
    while ($queue.Count -gt 0) {
        $curr = $queue.Dequeue()
        $items = Get-ChildItem -LiteralPath $curr -Force
        if ($items) {
            foreach ($child in $items) {
                if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Redirected $label entry: $($child.FullName)"
                }
                if ($child.PSIsContainer) {
                    $queue.Enqueue($child.FullName)
                } else {
                    $files.Add($child)
                }
            }
        }
    }
    return $files
}

try {
    $expected = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Hollow Ping Booster'
    if (-not [Environment]::Is64BitProcess -or [IO.Path]::GetFullPath($InstallDir).TrimEnd('\') -ine $expected) {
        throw 'Use the fixed Hollow Ping Booster directory with 64-bit PowerShell.'
    }
    if ((Test-Path $expected) -and ((Get-Item -LiteralPath $expected -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Redirected installation directory is not supported.'
    }
    $exe = Join-Path $expected 'gpb-service.exe'
    $quotedExe = '"' + $exe + '"'
    $service = Get-CimInstance Win32_Service -Filter "Name='HollowPingBooster'"
    if ($Uninstall) {
        $step = 'uninstall-service'
        if ($service) {
            if ($service.PathName -ine $quotedExe -or $service.StartName -ne 'LocalSystem') { throw 'Unexpected Hollow service registration; refusing removal.' }
            $controller = Get-Service HollowPingBooster
            try {
                if ($controller.Status -ne 'Stopped') { Stop-Service HollowPingBooster }
                $controller.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
            } finally { $controller.Dispose() }
            $removed = Invoke-CimMethod -InputObject $service -MethodName Delete
            if ($removed.ReturnValue -ne 0) { throw "Service deletion failed: $($removed.ReturnValue)" }
            if (Get-CimInstance Win32_Service -Filter "Name='HollowPingBooster'") { throw 'Service is pending deletion. Close Services and retry before deleting files.' }
        }
        Write-Output 'Hollow service removed. Shared Wintun and user configuration are preserved.'
        exit 0
    }
    if ($service) { throw 'HollowPingBooster already exists. Close Hollow, then uninstall the previous Hollow installation first. The vendor Game Ping Booster can remain installed.' }
    $data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'HollowPingBooster'
    if (Test-Path -LiteralPath $data) {
        [void](Get-SafeChildItems $data 'Hollow data')
        try {
            $dataAcl = Get-Acl -LiteralPath $data -ErrorAction Stop
            $ownerSid = $dataAcl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
            $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            if ($ownerSid -and $ownerSid -ne $currentSid -and $ownerSid -notin @('S-1-5-18', 'S-1-5-32-544')) {
                throw "Unsafe Hollow data directory owner ($ownerSid); refusing installation."
            }
        } catch [System.Security.SecurityException], [System.UnauthorizedAccessException] {
            # Restricted permissions (SYSTEM/Admin only) are safe
        }
    }
    if ($CheckOnly) {
        $existingFiles = Get-SafeChildItems $expected 'payload'
        if ($existingFiles.Count -gt 0) {
            # ponytail: recover only an intact previous Hollow payload with no service or verified partial copy against bundled manifest.
            $uninstallerFiles = @('unins000.exe','unins000.dat','unins000.msg')
            $manifest = $null
            $manifestHash = $null
            if ($PayloadManifest -and (Test-Path -LiteralPath $PayloadManifest)) {
                $manifest = Get-Content -LiteralPath $PayloadManifest -Raw | ConvertFrom-Json
                $manifestHash = (Get-FileHash -LiteralPath $PayloadManifest -Algorithm SHA256).Hash
            } elseif ((Test-Path -LiteralPath (Join-Path $expected 'BUILD-INFO.json')) -and (Test-Path -LiteralPath (Join-Path $expected 'SHA256SUMS.json'))) {
                $info = Get-Content -LiteralPath (Join-Path $expected 'BUILD-INFO.json') -Raw | ConvertFrom-Json
                if ($info.name -ne 'Hollow Ping Booster' -or $info.service -ne 'HollowPingBooster') { throw 'Unrecognized existing directory; refusing overwrite.' }
                $manifest = Get-Content -LiteralPath (Join-Path $expected 'SHA256SUMS.json') -Raw | ConvertFrom-Json
                foreach ($p in $manifest.PSObject.Properties) {
                    if ($p.Name -match '(^[\\/]|\.\.|:)') { throw 'Unsafe previous manifest path.' }
                    $targetPath = Join-Path $expected $p.Name
                    if (-not (Test-Path -LiteralPath $targetPath)) { throw "Incomplete previous payload missing: $($p.Name)" }
                    if ((Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash -ine $p.Value) { throw "Previous payload was modified: $($p.Name)" }
                }
            } else {
                throw 'Unrecognized existing directory; refusing overwrite.'
            }
            if (-not $manifest -or @($manifest.PSObject.Properties).Count -lt 10) { throw 'Empty or incomplete payload manifest.' }
            foreach ($file in $existingFiles) {
                $relative = $file.FullName.Substring($expected.Length + 1).Replace('\','/')
                if ($relative -in $uninstallerFiles) { continue }
                if ($relative -ieq 'SHA256SUMS.json') {
                    if ($manifestHash -and (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ine $manifestHash) {
                        throw 'Previous payload was modified: SHA256SUMS.json'
                    }
                    continue
                }
                $prop = $manifest.PSObject.Properties[$relative]
                if (-not $prop) { throw "Unknown file in previous payload: $relative" }
                if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ine $prop.Value) {
                    throw "Previous payload was modified: $relative"
                }
            }
        }
        Write-Output 'Install checks passed. Vendor service was not stopped or modified.'
        exit 0
    }
    $step = 'verify-payload'
    [void](Get-SafeChildItems $expected 'payload')
    $manifest = Get-Content (Join-Path $expected 'SHA256SUMS.json') -Raw | ConvertFrom-Json
    if (@($manifest.PSObject.Properties).Count -lt 10) { throw 'Empty or incomplete payload manifest.' }
    foreach ($p in $manifest.PSObject.Properties) {
        if ($p.Name -match '(^[\\/]|\.\.|:)') { throw 'Unsafe manifest path.' }
        $path = [IO.Path]::GetFullPath((Join-Path $expected $p.Name))
        if (-not $path.StartsWith($expected + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid manifest path.' }
        if ((Get-FileHash $path -Algorithm SHA256).Hash -ine $p.Value) { throw "Installed payload hash mismatch: $($p.Name)" }
    }
    $step = 'verify-driver'
    $signature = Get-AuthenticodeSignature (Join-Path $expected 'wintun.dll')
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=WireGuard LLC') { throw "Official Wintun driver signature check failed ($($signature.Status)). Do not disable signature checks; inspect the Windows certificate trust." }
    $step = 'protect-data'
    if (Test-Path -LiteralPath $data) {
        [void](Get-SafeChildItems $data 'Hollow data')
    } else {
        New-Item -ItemType Directory $data | Out-Null
    }
    & "$env:SystemRoot\System32\icacls.exe" $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F'
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect service data directory.' }
    $step = 'register-service'
    $registration = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments @{
        Name = 'HollowPingBooster'; DisplayName = 'Hollow Ping Booster'; PathName = $quotedExe
        ServiceType = [byte]16; ErrorControl = [byte]1; StartMode = 'Automatic'; StartName = 'LocalSystem'; DesktopInteract = $false
    }
    if ($registration.ReturnValue -ne 0) { throw "Service registration failed: $($registration.ReturnValue)" }
    $created = $true
    $live = Get-CimInstance Win32_Service -Filter "Name='HollowPingBooster'"
    if ($live.PathName -ine $quotedExe -or $live.StartName -ne 'LocalSystem') { throw 'Service registration readback failed.' }
    $step = 'start-service'
    $startAttempted = $true
    Start-Service HollowPingBooster
    $controller = Get-Service HollowPingBooster
    try { $controller.WaitForStatus('Running', [TimeSpan]::FromSeconds(30)) }
    finally { $controller.Dispose() }
    $step = 'check-ipc'
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'HollowPingBooster', [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(60000)
        $reader = New-Object IO.StreamReader($pipe)
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait(10000) -or -not $task.Result) { throw 'Service IPC startup timed out.' }
        $status = $task.Result | ConvertFrom-Json
        if ($status.v -ne 2 -or -not $status.fullTunnel -or $status.state -notin @(0,'Disconnected')) { throw 'Service did not start disconnected with the expected Hollow full-tunnel IPC.' }
    } finally { $pipe.Dispose() }
    Write-Output 'Hollow service installed and IPC verified. No PSK was bundled and no tunnel was connected.'
} catch {
    $failure = $_.Exception.Message
    if ($created) {
        $controller = $null
        try {
            if ($startAttempted) {
                try {
                    $controller = Get-Service HollowPingBooster
                    if ($controller.Status -ne 'Stopped') { Stop-Service HollowPingBooster }
                    $controller.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
                } catch {
                    $failure += '; service stop failed: ' + $_.Exception.Message
                } finally {
                    if ($controller) { $controller.Dispose() }
                }
            }
            $own = Get-CimInstance Win32_Service -Filter "Name='HollowPingBooster'"
            if ($own) {
                if ($own.PathName -ieq $quotedExe -and $own.StartName -eq 'LocalSystem') {
                    try {
                        $deleted = Invoke-CimMethod -InputObject $own -MethodName Delete
                        if ($deleted.ReturnValue -ne 0) {
                            $failure += "; service deletion failed: $($deleted.ReturnValue)"
                        } elseif (Get-CimInstance Win32_Service -Filter "Name='HollowPingBooster'") {
                            $failure += '; service is pending deletion'
                        }
                    } catch {
                        $failure += '; service deletion failed: ' + $_.Exception.Message
                    }
                } else {
                    $failure += '; service ownership changed; rollback refused.'
                }
            }
        } catch {
            $failure += '; service rollback failed: ' + $_.Exception.Message
        }
    }
    Write-Output ('ERROR [' + $step + ']: ' + $failure)
    exit 1
}
