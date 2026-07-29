$ErrorActionPreference = 'Stop'

$installDir = 'C:\Programs'
$stageDir = Join-Path $PSScriptRoot 'publish\gpt'
$projectPath = Join-Path $PSScriptRoot 'limits.csproj'
$stagedExe = Join-Path $stageDir 'gpt.exe'
$targetExe = Join-Path $installDir 'gpt.exe'
$trayIconGuids = @(
    '{2A642A8D-169A-4035-AD86-EA43B5E87764}',
    '{4654B565-47C7-49AF-A257-8F26D82C0EC0}',
    '{918BD040-6A80-4B43-AE66-13A8F5BB1D57}'
)

function Stop-Gpt {
    $running = @(Get-Process -Name gpt -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return
    }

    if (Test-Path -LiteralPath $targetExe) {
        try {
            $shutdown = Start-Process -FilePath $targetExe -ArgumentList '--shutdown' -WindowStyle Hidden -PassThru
            Wait-Process -InputObject $shutdown -Timeout 3 -ErrorAction SilentlyContinue
        } catch {
        }

        $deadline = (Get-Date).AddSeconds(5)
        do {
            Start-Sleep -Milliseconds 200
            $running = @(Get-Process -Name gpt -ErrorAction SilentlyContinue)
        } while ($running.Count -gt 0 -and (Get-Date) -lt $deadline)
    }

    $running = @(Get-Process -Name gpt -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
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

Stop-Gpt

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -LiteralPath $stagedExe -Destination $targetExe -Force

Start-Process -FilePath $targetExe
if (Promote-LimitsTrayIcons) {
    Stop-Gpt
    Start-Process -FilePath $targetExe
}

Write-Host "Installed and started $targetExe"
