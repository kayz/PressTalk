param(
  [string]$ModelRoot = "models",
  [switch]$SkipDownload
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
$targetRoot = Join-Path $repoRoot $ModelRoot

$models = @(
  "Qwen/Qwen3-ASR-1.7B",
  "Qwen/Qwen3-ASR-0.6B",
  "Qwen/Qwen3-0.6B"
)

New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null

if ($SkipDownload) {
  Write-Host "Model download skipped."
  Write-Host "Expected models:" 
  $models | ForEach-Object { Write-Host "  $_" }
  Write-Host "Model root: $targetRoot"
  exit 0
}

Write-Host "Downloading Qwen models to: $targetRoot"

$pythonCode = @"
from huggingface_hub import snapshot_download
models = [
    'Qwen/Qwen3-ASR-1.7B',
    'Qwen/Qwen3-ASR-0.6B',
    'Qwen/Qwen3-0.6B',
]
for model in models:
    path = snapshot_download(repo_id=model, local_dir=r'$targetRoot', local_dir_use_symlinks=False)
    print(f'{model} -> {path}')
"@

python -m pip install --upgrade huggingface_hub
$pythonCode | python -

Write-Host "Model download finished."
