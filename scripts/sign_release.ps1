#Requires -Version 7.4
# Per-release signing step, run AFTER the tag build has published its assets:
#   pwsh scripts/sign_release.ps1 -Tag v6.7.0
# Downloads the two published assets, hashes them LOCALLY (a compromised CI can therefore
# never get false hashes signed), writes update_manifest.json, signs it with the offline
# private key, and uploads manifest + signature to the release. Until this step runs,
# the check fails safe: no update is ever offered (auto checks stay silent; a manual
# check shows the failure message).
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$KeyPath = (Join-Path $env:USERPROFILE ".nexus-signing\nexus_update_private.pem"),
    [string]$Repo = "T3SoD/NexusApp"
)
$ErrorActionPreference = "Stop"

if ($Tag -notmatch '^v\d+\.\d+\.\d+$') { throw "Tag must look like v6.7.0 (got '$Tag')." }
if (-not (Test-Path $KeyPath)) { throw "Private key not found at $KeyPath. Run scripts/generate_update_keys.ps1 first." }
$version = $Tag.Substring(1)

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("nexus-sign-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $assets = @("Nexus_Setup.exe", "NexusApp_portable.zip")
    foreach ($a in $assets) {
        gh release download $Tag --repo $Repo --pattern $a --dir $work
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $work $a))) { throw "Couldn't download $a from release $Tag." }
    }

    $entries = foreach ($a in $assets) {
        $f = Join-Path $work $a
        [ordered]@{
            name   = $a
            sha256 = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
            size   = (Get-Item $f).Length
        }
    }
    $manifest = [ordered]@{
        schema    = 1
        version   = $version
        published = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        assets    = @($entries)
    }

    $manifestPath = Join-Path $work "update_manifest.json"
    ($manifest | ConvertTo-Json -Depth 4) | Set-Content -Path $manifestPath -Encoding utf8NoBOM -NoNewline

    # Read the key file outside the try, so a missing or unreadable file reports itself instead
    # of being blamed on the passphrase.
    $keyPem = Get-Content $KeyPath -Raw
    $pass = ConvertFrom-SecureString (Read-Host -AsSecureString "Private key passphrase") -AsPlainText
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
    try { $ecdsa.ImportFromEncryptedPem($keyPem, $pass) }
    catch { throw "Couldn't unlock the private key. Wrong passphrase, or the key is not protected yet (run scripts/protect_update_key.ps1 once)." }
    $bytes = [System.IO.File]::ReadAllBytes($manifestPath)
    $sig = $ecdsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    # Prove the signature verifies HERE, before anything is written or uploaded. A bad key file
    # or a mangled read would otherwise publish a manifest that every client refuses, which the
    # app reports only as a failed check.
    if (-not $ecdsa.VerifyData($bytes, $sig, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) {
        throw "Self-verification of the new signature failed. Nothing was uploaded."
    }
    $sigPath = "$manifestPath.sig"
    Set-Content -Path $sigPath -Value ([Convert]::ToBase64String($sig)) -Encoding ascii -NoNewline

    gh release upload $Tag $manifestPath $sigPath --repo $Repo --clobber
    if ($LASTEXITCODE -ne 0) { throw "Couldn't upload the signed manifest to release $Tag." }
    Write-Host "Signed manifest uploaded for $Tag."
}
finally {
    # Best effort: a cleanup failure must never mask the real error that got us here.
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
