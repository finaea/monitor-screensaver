# Produces the two Windows release artifacts in .\publish:
#
#   MonitorScreenSaver.exe     1.4 MB   needs the .NET 9 Desktop Runtime
#   MonitorScreenSaverSC.exe   146 MB   runtime bundled, nothing to install
#
#   .\tools\publish.ps1                 both (what the release workflow runs)
#   .\tools\publish.ps1 -Variant Fdd     just the small one, for a quick local build
#
# Why both. The runtime is 99% of the self-contained exe (151.8 of 153.3 MB), and it is
# also what makes the file look like malware to a behavioural scanner: a 146 MB unsigned
# binary that unpacks native libraries into %TEMP%\.net at startup. The small build has
# neither problem. The big one stays for locked-down machines where installing a runtime
# is not an option.
param(
    [ValidateSet('Both', 'Fdd', 'SelfContained')]
    [string]$Variant = 'Both'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\MonitorScreenSaver.Windows\MonitorScreenSaver.Windows.csproj"
$out = Join-Path $root "publish"

function Publish-Variant {
    param([bool]$SelfContained, [string]$Destination)

    # Each variant publishes into its own directory, because both produce a file called
    # MonitorScreenSaver.exe and the second would otherwise overwrite the first.
    $stage = Join-Path $out (".stage-" + $(if ($SelfContained) { "sc" } else { "fdd" }))
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

    $args = @(
        'publish', $proj
        '-c', 'Release'
        '-r', 'win-x64'
        "--self-contained", $SelfContained.ToString().ToLower()
        '-p:PublishSingleFile=true'
        '-p:DebugType=none'          # no .pdb next to a release artifact
        '-o', $stage
    )

    # Only meaningful for the self-contained build; the framework-dependent one has no
    # native libraries of its own to extract.
    if ($SelfContained) { $args += '-p:IncludeNativeLibrariesForSelfExtract=true' }

    # Note: EnableCompressionInSingleFile is deliberately OFF. It shrinks the exe by ~40%
    # but the bundle is decompressed into memory at startup, which measured +75 MB of
    # private bytes at idle. Disk is cheaper than RAM for a tray app.

    dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Destination" }

    # Move-Item -Force reports "Cannot create a file when that file already exists" when the
    # destination is locked, which says nothing about why. It is almost always this app
    # running out of .\publish, so name that instead.
    $target = Join-Path $out $Destination
    if (Test-Path $target) {
        try {
            Remove-Item $target -Force -ErrorAction Stop
        }
        catch {
            $running = Get-Process -Name MonitorScreenSaver -ErrorAction SilentlyContinue
            $who = if ($running) { " It is running as pid $($running.Id -join ', ')." } else { "" }
            throw "Cannot replace $Destination - the file is in use.$who Exit the tray app and run this again."
        }
    }

    Move-Item (Join-Path $stage "MonitorScreenSaver.exe") $target
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force $out | Out-Null

if ($Variant -in 'Both', 'Fdd') {
    Publish-Variant -SelfContained $false -Destination "MonitorScreenSaver.exe"
}
if ($Variant -in 'Both', 'SelfContained') {
    Publish-Variant -SelfContained $true -Destination "MonitorScreenSaverSC.exe"
}

Write-Host ""
Get-ChildItem $out -Filter "MonitorScreenSaver*.exe" | ForEach-Object {
    "{0,-38} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)
}
