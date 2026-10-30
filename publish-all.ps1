#!/usr/bin/env pwsh
# RemoteKSP build script: native KSP DLLs + single-file Client / Server executables.
#
#   .\publish-all.ps1                  # build into Publish\
#   .\publish-all.ps1 -Version 2.2.0   # stamp the version into the executables (used by the release workflow)
#   .\publish-all.ps1 -Test            # also build the native probes and run tests/SecurityRegression
#
# Writes Publish\SHA256SUMS.txt for the two executables.

param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Step([string]$Title, [scriptblock]$Action) {
    Write-Host "`n=== $Title ===" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Title failed (exit code $LASTEXITCODE)" }
}

# Shared publish arguments for Client and Server
$publishArgs = @('-c', 'Release', '-r', 'win-x64', '--self-contained', '--nologo',
                 '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
if ($Version) {
    # Assembly/file versions must be numeric; a pre-release suffix only goes into the informational version
    $numeric = ($Version -replace '-.*$', '') + '.0'
    $publishArgs += "-p:Version=$Version", "-p:AssemblyVersion=$numeric", "-p:FileVersion=$numeric"
}

Invoke-Step 'Native KSP' { cmd /c native\ksp\build.cmd }

# The output exe cannot be overwritten while the server GUI or a service installed from Publish\ is running
$serverExe = 'Publish\Server\RemoteKSP.Server.exe'
if (Test-Path $serverExe) {
    try { [IO.File]::Open((Resolve-Path $serverExe), 'Open', 'ReadWrite', 'None').Dispose() }
    catch { throw "$serverExe is in use. Close the server GUI, or stop a service installed from this folder (sc.exe stop RemoteKSP)." }
}

Invoke-Step 'Client' { dotnet publish src/RemoteKSP.Client/RemoteKSP.Client.csproj @publishArgs -o Publish/Client }
Invoke-Step 'Server' { dotnet publish src/RemoteKSP.Server/RemoteKSP.Server.csproj @publishArgs -o Publish/Server }

if ($Test) {
    Invoke-Step 'Native test probes' { cmd /c tests\native\build.cmd }
    Invoke-Step 'Regression tests (build)' {
        dotnet build tests/SecurityRegression/SecurityRegression.csproj -c Release --nologo -m:1
    }
    Invoke-Step 'Regression tests (run)' {
        dotnet run --project tests/SecurityRegression/SecurityRegression.csproj -c Release --no-build
    }
}

$artifacts = @('Publish\Client\RemoteKSP.Client.exe', 'Publish\Server\RemoteKSP.Server.exe')
$sums = foreach ($path in $artifacts) {
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $path -Leaf)"
}
Set-Content -Path Publish\SHA256SUMS.txt -Value $sums -Encoding ascii

Write-Host "`n=== Build complete ===" -ForegroundColor Green
foreach ($path in $artifacts + @('native\ksp\RemoteKSP_x64.dll', 'native\ksp\RemoteKSP_x86.dll')) {
    Write-Host ("  {0,-40} {1,15:N0} bytes" -f $path, (Get-Item $path).Length) -ForegroundColor Yellow
}
Write-Host "`nSHA256:"
$sums | ForEach-Object { Write-Host "  $_" }
