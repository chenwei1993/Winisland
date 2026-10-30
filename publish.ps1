param(
    [switch]$Compressed
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "WinIsland.csproj"
$output = Join-Path $root "dist"

$dotnetExe = $null
$command = Get-Command dotnet -ErrorAction SilentlyContinue
if ($command -and $command.Source) {
    $dotnetExe = $command.Source
}

if (-not $dotnetExe -or -not (Test-Path $dotnetExe)) {
    $candidates = @(
        (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"),
        (Join-Path $env:ProgramFiles "dotnet\dotnet.exe")
    )

    $pf86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if ($pf86) {
        $candidates += (Join-Path $pf86 "dotnet\dotnet.exe")
    }

    $dotnetExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $dotnetExe) {
    throw "dotnet was not found. Install the .NET 10 SDK or run the publish command manually."
}

# Uncompressed is the default: the compressed single-file bundle is much
# smaller on disk but roughly doubles private memory because .NET has to
# decompress the runtime before using it.
$compression = if ($Compressed) { "true" } else { "false" }

& $dotnetExe publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=$compression `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $output "WinIsland.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ""
Write-Host "Standalone x64 build:" -ForegroundColor Green
Write-Host "  $exe ($size MB)"
Write-Host "No .NET runtime installation is required." -ForegroundColor Green
if ($Compressed) {
    Write-Host "Compressed mode: smallest exe, but higher RAM usage." -ForegroundColor Yellow
} else {
    Write-Host "Uncompressed mode: larger exe, lower RAM usage." -ForegroundColor Green
    Write-Host "Use -Compressed if you prefer the roughly 73 MB exe instead." -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "For ARM64 Windows, change -r win-x64 to -r win-arm64."
