# Builds dist\PCStatusSetup-<version>.exe: publishes x64 + ARM64 single-file exes, then compiles the installer.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

[xml]$proj = Get-Content "$root\PCStatus.csproj"
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" }

foreach ($rid in 'win-x64', 'win-arm64') {
    Write-Host "Publishing $rid..."
    & $dotnet publish "$root\PCStatus.csproj" -c Release -r $rid -o "$root\publish\$rid" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "publish $rid failed" }
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

Write-Host "Compiling installer (v$version)..."
& $iscc /Q "/DAppVersion=$version" "$PSScriptRoot\PCStatus.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem "$root\dist\PCStatusSetup-$version.exe"
