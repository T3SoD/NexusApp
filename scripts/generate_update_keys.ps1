#Requires -Version 7.4
# One-time ceremony: create the update-signing keypair. The PRIVATE key stays on the
# maintainer's machine (default %USERPROFILE%\.nexus-signing, outside any repo) and must be
# backed up offline; the PUBLIC key gets pasted into NexusApp/Services/UpdateVerifier.cs.
# Refuses to overwrite an existing private key: rotating the key is a deliberate act.
# The private key is passphrase-protected from creation, so signing always needs a phrase
# typed at a keyboard and nothing automated can sign.
param(
    [string]$KeyDir = (Join-Path $env:USERPROFILE ".nexus-signing")
)
$ErrorActionPreference = "Stop"

$priv = Join-Path $KeyDir "nexus_update_private.pem"
$pub  = Join-Path $KeyDir "nexus_update_public.pem"
if (Test-Path $priv) { throw "Refusing to overwrite the existing private key at $priv. Delete it yourself if you really mean to rotate." }

New-Item -ItemType Directory -Force -Path $KeyDir | Out-Null
$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)

# 12 characters minimum, not 8: at this KDF cost an 8-character human-chosen phrase falls to
# offline guessing in hours, and the phrase is the entire defence if any copy of this file leaks.
$p1 = ConvertFrom-SecureString (Read-Host -AsSecureString "New passphrase (12 characters minimum)") -AsPlainText
$p2 = ConvertFrom-SecureString (Read-Host -AsSecureString "Confirm the new passphrase") -AsPlainText
if ($p1 -cne $p2) { throw "Passphrases do not match. Nothing was changed." }
if ($p1.Length -lt 12) { throw "Use at least 12 characters. Nothing was changed." }

$pbe = [System.Security.Cryptography.PbeParameters]::new(
    [System.Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    600000)

Set-Content -Path $priv -Value $ecdsa.ExportEncryptedPkcs8PrivateKeyPem($p1, $pbe) -Encoding ascii -NoNewline
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
