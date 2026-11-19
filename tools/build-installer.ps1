# Builds the CoreWatch installer: publish\CoreWatch-<version>-win-x64-setup.exe.
# Needs the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
param(
    # Path to ISCC.exe, if it is not on PATH or in a usual install folder.
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $root 'obj\installer\app'
$out = Join-Path $root 'publish'

if (-not $Iscc) {
    $onPath = Get-Command iscc -ErrorAction SilentlyContinue
    if ($onPath) { $Iscc = $onPath.Source }
    else {
        $Iscc = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}
if (-not $Iscc -or -not (Test-Path $Iscc)) {
    throw 'ISCC.exe was not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup) or pass -Iscc <path>.'
}

# The installer carries a self-contained build as plain files: no .NET install needed,
# and Inno Setup compresses loose files better than a packed single-file exe.
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish (Join-Path $root 'CoreWatch.csproj') -c Release --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false -o $app -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

& $Iscc /Q "/DSource=$app" "/DOutDir=$out" (Join-Path $root 'installer\CoreWatch.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)." }

Get-ChildItem $out -Filter '*-setup.exe' | Sort-Object LastWriteTime | Select-Object -Last 1 |
    ForEach-Object { '{0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }
