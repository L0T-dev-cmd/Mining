$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet publish src/Mining.Desktop/Mining.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/windows
    if ($LASTEXITCODE -ne 0) { throw 'La compilación del ejecutable falló.' }
    $exe = Join-Path $repo 'artifacts/windows/MineriaBitcoin.exe'
    if (!(Test-Path $exe)) { throw 'No se generó MineriaBitcoin.exe.' }
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  MineriaBitcoin.exe" | Set-Content (Join-Path $repo 'artifacts/windows/SHA256SUMS.txt') -Encoding ascii
    Copy-Item README.md artifacts/windows/README.md
    Copy-Item CHANGELOG.md artifacts/windows/CHANGELOG.md
    New-Item -ItemType Directory -Force artifacts/windows/docs | Out-Null
    Copy-Item docs/*.md artifacts/windows/docs/
    Write-Host 'Ejecutable y checksum disponibles en artifacts/windows.'
} finally { Pop-Location }
