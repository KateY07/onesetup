<#
  Builds onesetup-setup.exe: OneSetup packaged by OneSetup.
  1. publishes onesetup.exe (self-contained, single file; the UI shell is compiled and embedded by the build)
  2. stages onesetup.exe + installer\install.bat + installer\set-user-path.ps1
  3. packs the stage with the freshly built onesetup.exe --ui
  Usage: installer\build-installer.ps1 [-Output dist\onesetup-setup.exe] [-Level 9]
#>
param(
    [string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\onesetup-setup.exe'),
    [ValidateRange(0, 9)][int]$Level = 9
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repo 'publish'
$stage = Join-Path $repo 'obj\installer-stage'

dotnet publish (Join-Path $repo 'onesetup.csproj') -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o $publish
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$null = New-Item -ItemType Directory -Path $stage
Copy-Item (Join-Path $publish 'onesetup.exe') $stage
Copy-Item (Join-Path $PSScriptRoot 'install.bat'), (Join-Path $PSScriptRoot 'set-user-path.ps1') $stage

if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Force }
& (Join-Path $publish 'onesetup.exe') $stage -o $Output -l $Level --ui
if ($LASTEXITCODE) { throw 'packing failed' }
$hash = (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$Output.sha256", "$hash *$(Split-Path $Output -Leaf)`r`n", [Text.Encoding]::ASCII)
Write-Output $Output
Write-Output $hash
