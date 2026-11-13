$ErrorActionPreference = 'Stop'

$credentialsPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'limits\unet-credentials.json'
$accounts = @()

for ($index = 1; $index -le 2; $index++) {
    $username = Read-Host "UNET account $index username (leave blank to finish)"
    if ([string]::IsNullOrWhiteSpace($username)) {
        break
    }

    $securePassword = Read-Host "UNET account $index password" -AsSecureString
    $passwordPointer = [IntPtr]::Zero
    try {
        $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
        $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
        $passwordBytes = [Text.Encoding]::UTF8.GetBytes($password)
        try {
            $protectedPassword = [Security.Cryptography.ProtectedData]::Protect(
                $passwordBytes,
                $null,
                [Security.Cryptography.DataProtectionScope]::CurrentUser)
        }
        finally {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($passwordBytes)
        }

        $accounts += [pscustomobject]@{
            username = $username.Trim()
            passwordProtected = [Convert]::ToBase64String($protectedPassword)
        }
    }
    finally {
        if ($passwordPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
        }
    }
}

if ($accounts.Count -eq 0) {
    throw 'At least one UNET account is required.'
}

$directory = Split-Path -Parent $credentialsPath
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$json = [pscustomobject]@{ accounts = $accounts } | ConvertTo-Json -Depth 4
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($credentialsPath, $json, $utf8NoBom)
Write-Host "Encrypted UNET credentials saved to $credentialsPath"
