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

Write-Host "Setting up FunASR runtime dependencies..."

Invoke-Step -Description "Upgrade pip" -Action { & $Python -m pip install --upgrade pip }

$hasNvidia = [bool](Get-Command nvidia-smi -ErrorAction SilentlyContinue)
if ($hasNvidia) {
  Invoke-Step -Description "Install CUDA torch + torchaudio (cu124)" -Action { & $Python -m pip install --upgrade --index-url https://download.pytorch.org/whl/cu124 torch torchaudio }
}
else {
  Invoke-Step -Description "Install CPU torch + torchaudio" -Action { & $Python -m pip install --upgrade --index-url https://download.pytorch.org/whl/cpu torch torchaudio }
}

Invoke-Step -Description "Install FunASR toolkit" -Action { & $Python -m pip install --upgrade funasr }
Invoke-Step -Description "Install runtime extras" -Action { & $Python -m pip install --upgrade modelscope soundfile librosa }

Write-Host "FunASR dependency setup finished."
