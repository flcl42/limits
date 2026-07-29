$ErrorActionPreference = 'Stop'

$installDir = 'D:\Programs'
$stageDir = Join-Path $PSScriptRoot 'publish\limits'
$projectPath = Join-Path $PSScriptRoot 'limits.csproj'
$stagedExe = Join-Path $stageDir 'limits.exe'
$targetExe = Join-Path $installDir 'limits.exe'
$oldExeTargets = @(
    'C:\Programs\gpt.exe',
    'D:\Programs\gpt.exe'
)
$oldStartupShortcutNames = @(
    'gpt.lnk',
    'gptcheck.lnk'
)
$trayIconGuids = @(
    '{2A642A8D-169A-4035-AD86-EA43B5E87764}',
    '{4654B565-47C7-49AF-A257-8F26D82C0EC0}',
    '{918BD040-6A80-4B43-AE66-13A8F5BB1D57}'
)

function Get-LimitsProcesses {
    @(Get-Process -Name gpt, limits -ErrorAction SilentlyContinue)
}

function Stop-Limits {
    $running = @(Get-LimitsProcesses)
    if ($running.Count -eq 0) {
        return
    }

    $shutdownExeTargets = @($targetExe) + $oldExeTargets | Select-Object -Unique
    foreach ($shutdownExe in $shutdownExeTargets) {
        if (-not (Test-Path -LiteralPath $shutdownExe)) {
            continue
        }

        try {
            $shutdown = Start-Process -FilePath $shutdownExe -ArgumentList '--shutdown' -WindowStyle Hidden -PassThru
            Wait-Process -InputObject $shutdown -Timeout 3 -ErrorAction SilentlyContinue
        } catch {
        }
    }

    $deadline = (Get-Date).AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 200
        $running = @(Get-LimitsProcesses)
    } while ($running.Count -gt 0 -and (Get-Date) -lt $deadline)

    $running = @(Get-LimitsProcesses)
    if ($running.Count -gt 0) {
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
}

function Remove-OldGptInstallations {
    foreach ($oldExe in ($oldExeTargets | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $oldExe) {
            Remove-Item -LiteralPath $oldExe -Force
        }
    }
}

function Set-LimitsStartupShortcut {
    $startupDir = [Environment]::GetFolderPath('Startup')
    if ([string]::IsNullOrWhiteSpace($startupDir)) {
        return
    }

    New-Item -ItemType Directory -Path $startupDir -Force | Out-Null
    $shortcutPath = Join-Path $startupDir 'limits.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $targetExe
    $shortcut.WorkingDirectory = $installDir
    $shortcut.IconLocation = "$targetExe,0"
    $shortcut.Description = 'limits tray usage monitor'
    $shortcut.Save()

    foreach ($shortcutName in $oldStartupShortcutNames) {
        $oldShortcutPath = Join-Path $startupDir $shortcutName
        if (Test-Path -LiteralPath $oldShortcutPath) {
            Remove-Item -LiteralPath $oldShortcutPath -Force
        }
    }
}

function Promote-LimitsTrayIcons {
    $settingsPath = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        return $false
    }

    $changed = $false
    $deadline = (Get-Date).AddSeconds(8)
    do {
        $seen = @{}
        Get-ChildItem -LiteralPath $settingsPath -ErrorAction SilentlyContinue | ForEach-Object {
            $properties = Get-ItemProperty -LiteralPath $_.PSPath
            $iconGuid = [string]$properties.IconGuid
            if ($properties.ExecutablePath -ieq $targetExe -and $trayIconGuids -contains $iconGuid.ToUpperInvariant()) {
                $seen[$iconGuid.ToUpperInvariant()] = $true
                if ($properties.IsPromoted -ne 1) {
                    New-ItemProperty -LiteralPath $_.PSPath -Name IsPromoted -Value 1 -PropertyType DWord -Force | Out-Null
                    $changed = $true
                }
            }
        }

        if ($seen.Count -ge $trayIconGuids.Count) {
            break
        }

        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    return $changed
}

dotnet publish $projectPath -c Release -r win-x64 -o $stageDir -p:PublishAot=true -p:SelfContained=true -p:InvariantGlobalization=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $stagedExe)) {
    throw "Published executable was not found: $stagedExe"
}

Stop-Limits

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Remove-OldGptInstallations
Copy-Item -LiteralPath $stagedExe -Destination $targetExe -Force
Set-LimitsStartupShortcut

Start-Process -FilePath $targetExe
if (Promote-LimitsTrayIcons) {
    Stop-Limits
    Start-Process -FilePath $targetExe
}

Write-Host "Installed and started $targetExe"
