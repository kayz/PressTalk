param(
  [switch]$Strict
)

$ErrorActionPreference = "Stop"

Write-Host "Checking PressTalk prerequisites..."

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
  Write-Warning "dotnet SDK is not installed."
  if ($Strict) {
    exit 1
  }
}
else {
  Write-Host "dotnet: $($dotnet.Source)"

  $sdks = & dotnet --list-sdks 2>$null
  if (-not $sdks) {
    Write-Warning "No .NET SDK found. Install .NET SDK 8+."
    if ($Strict) {
      exit 1
    }
  }
  else {
    Write-Host ".NET SDKs:"
    $sdks | ForEach-Object { Write-Host "  $_" }
  }
}

Write-Host ""
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
  Write-Warning "python is not installed."
  if ($Strict) {
    exit 1
  }
}
else {
  Write-Host "python: $($python.Source)"
  & python --version
}

Write-Host "Prerequisite check finished."
