param(
  [switch]$SkipDownload
)

$ErrorActionPreference = "Stop"

$models = @(
  "iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-online",
  "iic/speech_campplus_sv_zh-cn_16k-common"
)

if ($SkipDownload) {
  Write-Host "Model download skipped."
  Write-Host "Expected models:"
  $models | ForEach-Object { Write-Host "  $_" }
  exit 0
}

Write-Host "Pre-downloading FunASR models to local cache..."

$pythonCode = @"
from modelscope.hub.snapshot_download import snapshot_download
models = [
    'iic/speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-online',
    'iic/speech_campplus_sv_zh-cn_16k-common',
]
for model in models:
    path = snapshot_download(model)
    print(f'{model} -> {path}')
"@

python -m pip install --upgrade modelscope
$pythonCode | python -

Write-Host "Model download finished."
