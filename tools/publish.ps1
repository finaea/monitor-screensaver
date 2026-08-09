# Produces a single self-contained MonitorDim.exe in .\publish
$root = Split-Path $PSScriptRoot -Parent

dotnet publish (Join-Path $root "MonitorDim.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $root "publish")

# Note: EnableCompressionInSingleFile is deliberately OFF. It shrinks the exe by
# ~40% but the bundle is decompressed into memory at startup, which measured
# +75 MB of private bytes at idle. Disk is cheaper than RAM for a tray app.

if ($LASTEXITCODE -eq 0) {
    $exe = Join-Path $root "publish\MonitorDim.exe"
    "`nPublished: $exe  ($([Math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"
}
