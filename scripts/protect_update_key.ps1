#Requires -Version 7.4
# One-time step (re-run any time to change the phrase): encrypt the update-signing private
# key so signing requires a passphrase typed at a keyboard. Non-interactive sessions cannot
# answer the prompt, so nothing automated can ever sign, even on this machine.
param(
    [string]$KeyPath = (Join-Path $env:USERPROFILE ".nexus-signing\nexus_update_private.pem")
)
$ErrorActionPreference = "Stop"

if (-not (Test-Path $KeyPath)) { throw "Private key not found at $KeyPath." }
$pem = Get-Content $KeyPath -Raw

$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
$alreadyEncrypted = $false
try { $ecdsa.ImportFromPem($pem) } catch { $alreadyEncrypted = $true }
if ($alreadyEncrypted) {
    $old = ConvertFrom-SecureString (Read-Host -AsSecureString "Current passphrase") -AsPlainText
    $ecdsa.ImportFromEncryptedPem($pem, $old)
}

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
$encrypted = $ecdsa.ExportEncryptedPkcs8PrivateKeyPem($p1, $pbe)

# Prove the round trip works BEFORE overwriting the only copy of the key: the new file must
# both decrypt with the new phrase AND hold the same key, or the published public key stops
# matching what we can sign with.
$check = [System.Security.Cryptography.ECDsa]::Create()
$check.ImportFromEncryptedPem($encrypted, $p1)
if ($check.ExportSubjectPublicKeyInfoPem() -cne $ecdsa.ExportSubjectPublicKeyInfoPem()) { throw "Round-trip check produced a different key. Nothing was changed." }

Set-Content -Path $KeyPath -Value $encrypted -Encoding ascii -NoNewline
Write-Host "Private key is now passphrase-protected."
Write-Host "Back THIS file up offline; restoring the backup will need the same passphrase."
