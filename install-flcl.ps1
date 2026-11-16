param(
    # Release tag to install. Empty = newest draft release in flcl42/limits.
    [string]$Tag = '',
    # Install location (same as install.ps1 so Windows logon keeps using it).
    [string]$TargetExe = 'C:\Programs\limits.exe'
)

$ErrorActionPreference = 'Stop'

$repo = 'flcl42/limits'
$assetName = 'limits-flcl.exe'

function Get-LatestFlclDraftTag {
    $tags = gh api "repos/$repo/releases?per_page=30" --jq '[.[] | select(.draft == true) | .tag_name][0]' 2>$null
    if ([string]::IsNullOrWhiteSpace($tags) -or $tags -eq 'null') {
        throw "No draft releases found in $repo (gh auth must belong to the repo owner)."
    }

    return $tags.Trim()
}

function Stop-LimitsInstances {
    $running = @(Get-Process -Name limits -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return
    }

    foreach ($exe in @($TargetExe, (Join-Path $PSScriptRoot 'publish\flcl-stage\limits.exe'))) {
        if (Test-Path -LiteralPath $exe) {
            try {
                $shutdown = Start-Process -FilePath $exe -ArgumentList '--shutdown' -WindowStyle Hidden -PassThru
                Wait-Process -InputObject $shutdown -Timeout 3 -ErrorAction SilentlyContinue
            } catch {
            }
        }
    }

    $deadline = (Get-Date).AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 200
        $running = @(Get-Process -Name limits -ErrorAction SilentlyContinue)
    } while ($running.Count -gt 0 -and (Get-Date) -lt $deadline)

    $running = @(Get-Process -Name limits -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = Get-LatestFlclDraftTag
    Write-Host "Using newest draft release: $Tag"
}

$stageDir = Join-Path ([IO.Path]::GetTempPath()) 'limits-flcl-install'
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
$stagedExe = Join-Path $stageDir $assetName

gh release download $Tag --repo $repo --pattern $assetName --dir $stageDir --clobber
if (-not (Test-Path -LiteralPath $stagedExe)) {
    throw "Asset $assetName was not downloaded from $Tag."
}

$digests = @(gh release view $Tag --repo $repo --json assets --jq '.assets[].digest' 2>$null) | Where-Object { $_ -match '^sha256:[0-9a-fA-F]+$' }
$actual = (Get-FileHash -LiteralPath $stagedExe -Algorithm SHA256).Hash
$matched = @($digests) | Where-Object { $_ -eq "sha256:$actual" }
if ($matched.Count -eq 0) {
    throw "Checksum mismatch for $assetName (sha256:$actual not listed in $Tag)."
}
Write-Host "Checksum OK."

Stop-LimitsInstances

New-Item -ItemType Directory -Path (Split-Path -Parent $TargetExe) -Force | Out-Null
Copy-Item -LiteralPath $stagedExe -Destination $TargetExe -Force
Write-Host "Installed $Tag to $TargetExe"

Start-ScheduledTask -TaskName 'limits'
Start-Sleep -Milliseconds 2000
$started = @(Get-Process -Name limits -ErrorAction SilentlyContinue)
if ($started.Count -eq 0) {
    throw "Installed, but the limits process did not start."
}

Write-Host "Running: $($started[0].Path) (pid $($started[0].Id))"
