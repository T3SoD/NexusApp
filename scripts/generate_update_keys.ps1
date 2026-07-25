#Requires -Version 7
# One-time ceremony: create the update-signing keypair. The PRIVATE key stays on the
# maintainer's machine (default %USERPROFILE%\.nexus-signing, outside any repo) and must be
# backed up offline; the PUBLIC key gets pasted into NexusApp/Services/UpdateVerifier.cs.
# Refuses to overwrite an existing private key: rotating the key is a deliberate act.
param(
    [string]$KeyDir = (Join-Path $env:USERPROFILE ".nexus-signing")
)
$ErrorActionPreference = "Stop"

$priv = Join-Path $KeyDir "nexus_update_private.pem"
$pub  = Join-Path $KeyDir "nexus_update_public.pem"
if (Test-Path $priv) { throw "Refusing to overwrite the existing private key at $priv. Delete it yourself if you really mean to rotate." }

New-Item -ItemType Directory -Force -Path $KeyDir | Out-Null
$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
Set-Content -Path $priv -Value $ecdsa.ExportPkcs8PrivateKeyPem() -Encoding ascii -NoNewline
Set-Content -Path $pub  -Value $ecdsa.ExportSubjectPublicKeyInfoPem() -Encoding ascii -NoNewline

# Best-effort: strip inherited ACLs so only the current user can access the private key.
# Full control, not read-only: the owner must be able to back up, rotate, and delete the
# key (read-only broke deletion outright, and an owner can always re-grant anyway).
try { icacls $priv /inheritance:r /grant:r "$($env:USERNAME):F" | Out-Null } catch { }

Write-Host "Private key: $priv"
Write-Host "Back it up somewhere offline. Losing it means shipping a new app version with a new key."
Write-Host ""
Write-Host "Public key (paste into PublicKeyPem in NexusApp/Services/UpdateVerifier.cs):"
Write-Host ""
Get-Content $pub
