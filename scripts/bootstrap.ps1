$ErrorActionPreference = "Stop"

Write-Host "PressTalk bootstrap started."

pwsh "$PSScriptRoot/check-prereqs.ps1"
pwsh "$PSScriptRoot/setup-qwen-runtime.ps1"
pwsh "$PSScriptRoot/download-model.ps1" -SkipDownload

Write-Host "Bootstrap finished."
