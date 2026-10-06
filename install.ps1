$ErrorActionPreference = 'Stop'

$installDir = 'C:\Programs'
$stageDir = Join-Path $PSScriptRoot 'publish\limits'
$projectPath = Join-Path $PSScriptRoot 'limits.csproj'
$stagedExe = Join-Path $stageDir 'limits.exe'
$targetExe = Join-Path $installDir 'limits.exe'
$elevatedTaskName = 'limits'
$oldExeTargets = @(
    'C:\Programs\gpt.exe'
)
$oldStartupShortcutNames = @(
    'gpt.lnk',
    'gptcheck.lnk'
)
$trayIconGuids = @(
    '{2A642A8D-169A-4035-AD86-EA43B5E87764}',
    '{4654B565-47C7-49AF-A257-8F26D82C0EC0}',
    '{918BD040-6A80-4B43-AE66-13A8F5BB1D57}',
    '{36F5599D-63AD-4D36-B75D-8498B2DF37BF}',
    '{7F0A7C1F-5D91-4C97-AEBF-6E0B4D4E2E1C}',
    '{C8D1D0C3-5B6A-4C0E-9A73-96ABF4C7785E}',
    '{A1D68E24-7E92-4BCB-A2BF-13F8A7E8C6D1}',
    '{B2E79F35-8FA3-4CDC-B3C0-24A9B8F9D7E2}',
    '{D3B6C0A4-7E21-4B50-9E1F-8E7D8DC33B2A}',
    '{D239A2D5-61F8-41A3-AE06-320BDA542C1C}'
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

function Set-LimitsElevatedStartup {
    $startupDir = [Environment]::GetFolderPath('Startup')
    if (-not [string]::IsNullOrWhiteSpace($startupDir)) {
        $shortcutPath = Join-Path $startupDir 'limits.lnk'
        if (Test-Path -LiteralPath $shortcutPath) {
            Remove-Item -LiteralPath $shortcutPath -Force
        }
    }

    foreach ($shortcutName in $oldStartupShortcutNames) {
        if (-not [string]::IsNullOrWhiteSpace($startupDir)) {
            $oldShortcutPath = Join-Path $startupDir $shortcutName
            if (Test-Path -LiteralPath $oldShortcutPath) {
                Remove-Item -LiteralPath $oldShortcutPath -Force
            }
        }
    }

    $existingTask = Get-ScheduledTask -TaskName $elevatedTaskName -ErrorAction SilentlyContinue
    if ($null -ne $existingTask -and
        $existingTask.Principal.RunLevel -eq 'Highest' -and
        $existingTask.Actions.Execute -ieq $targetExe) {
        return
    }

    $taskRun = '"' + $targetExe + '"'
    $taskArguments = @(
        '/Create',
        '/TN', $elevatedTaskName,
        '/TR', $taskRun,
        '/SC', 'ONLOGON',
        '/RL', 'HIGHEST',
        '/RU', $env:USERNAME,
        '/IT',
        '/F'
    )
    $taskProcess = Start-Process -FilePath "$env:SystemRoot\System32\schtasks.exe" `
        -Verb RunAs -ArgumentList $taskArguments -Wait -PassThru
    if ($taskProcess.ExitCode -ne 0) {
        throw "Could not register the elevated $elevatedTaskName logon task (exit code $($taskProcess.ExitCode))."
    }

    $task = Get-ScheduledTask -TaskName $elevatedTaskName -ErrorAction SilentlyContinue
    if ($null -eq $task -or $task.Principal.RunLevel -ne 'Highest') {
        throw "The elevated $elevatedTaskName logon task was not registered with the highest run level."
    }
}

function Start-LimitsElevated {
    Start-ScheduledTask -TaskName $elevatedTaskName
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
Set-LimitsElevatedStartup

Start-LimitsElevated
if (Promote-LimitsTrayIcons) {
    Stop-Limits
    Start-LimitsElevated
}

Write-Host "Installed and started $targetExe"
