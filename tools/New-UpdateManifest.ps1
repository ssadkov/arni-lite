param(
    [switch]$InitializeKey,
    [string]$Version,
    [string]$InstallerDirectory = (Join-Path $PSScriptRoot '..\dist'),
    [string]$MinimumWindows = '6.1.7601',
    [string]$KeyPath = (Join-Path $env:LOCALAPPDATA 'Arnion\ArniLite\ReleaseKeys\updates.dpapi')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$publicPath = Join-Path $PSScriptRoot '..\src\ArniLite.Core\UpdatesPublicKey.xml'
$provider = New-Object System.Security.Cryptography.CspParameters
$provider.ProviderType = 24
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072, $provider)
$rsa.PersistKeyInCsp = $false
try {
    if ($InitializeKey) {
        if (Test-Path -LiteralPath $KeyPath) { throw 'Signing key already exists. Do not replace it: installed clients trust the existing key.' }
        if (Test-Path -LiteralPath $publicPath) { throw 'Public trust key already exists. Initialize from a clean, intentional key setup only.' }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($KeyPath)) | Out-Null
        $privateBytes = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
        $protected = [Security.Cryptography.ProtectedData]::Protect($privateBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        [IO.File]::WriteAllBytes($KeyPath, $protected)
        [Array]::Clear($privateBytes, 0, $privateBytes.Length)
        [IO.File]::WriteAllText($publicPath, $rsa.ToXmlString($false), (New-Object Text.UTF8Encoding($false)))
        Write-Output 'Initialized DPAPI-protected local signing key and public application trust key.'
        return
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Specify a release version, for example 0.1.2.' }
    $privateBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($KeyPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try { $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($privateBytes)) }
    finally { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
    if ($rsa.ToXmlString($false) -ne [IO.File]::ReadAllText($publicPath)) { throw 'Signing key does not match the application trust key.' }
    $installers = @('x64', 'x86') | ForEach-Object {
        $name = "ArniLiteSetup-$Version-$_.exe"
        $path = Join-Path $InstallerDirectory $name
        $file = Get-Item -LiteralPath $path
        @{ Architecture = $_; FileName = $name; Bytes = $file.Length; Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $manifest = @{ Schema = 1; Version = $Version; MinimumWindows = $MinimumWindows; Installers = @($installers) } | ConvertTo-Json -Depth 5
    $data = [Text.Encoding]::UTF8.GetBytes($manifest)
    $signature = $rsa.SignData($data, 'SHA256')
    [IO.File]::WriteAllBytes((Join-Path $InstallerDirectory 'update.json'), $data)
    [IO.File]::WriteAllBytes((Join-Path $InstallerDirectory 'update.json.sig'), $signature)
    Write-Output 'Created update.json and update.json.sig. Upload both alongside the exact installers to the corresponding GitHub release.'
}
finally { $rsa.Dispose() }
