param(
  [string]$Python = "python"
)

$ErrorActionPreference = "Stop"

function Invoke-Step {
  param(
    [Parameter(Mandatory = $true)][string]$Description,
    [Parameter(Mandatory = $true)][scriptblock]$Action
  )

  Write-Host $Description
  & $Action
  if ($LASTEXITCODE -ne 0) {
    throw "Step failed: $Description"
  }
}

Write-Host "Setting up Qwen local runtime dependencies..."

Invoke-Step -Description "Upgrade pip" -Action { & $Python -m pip install --upgrade pip }

$installedTorch = $false
$hasNvidia = [bool](Get-Command nvidia-smi -ErrorAction SilentlyContinue)

if ($hasNvidia) {
  try {
    Invoke-Step -Description "Install CUDA torch (cu124)" -Action { & $Python -m pip install --upgrade --index-url https://download.pytorch.org/whl/cu124 torch }
    $installedTorch = $true
  }
  catch {
    Write-Warning "CUDA torch install failed, fallback to CPU torch."
  }
}

if (-not $installedTorch) {
  Invoke-Step -Description "Install CPU torch" -Action { & $Python -m pip install --upgrade --index-url https://download.pytorch.org/whl/cpu torch }
}

Invoke-Step -Description "Install Qwen ASR toolkit from source" -Action { & $Python -m pip install --upgrade git+https://github.com/QwenLM/Qwen3-ASR.git }
Invoke-Step -Description "Install extra runtime libs" -Action { & $Python -m pip install --upgrade qwen-omni-utils "transformers==4.57.6" "huggingface-hub==0.36.2" accelerate sentencepiece }

Write-Host "Dependency setup finished."
