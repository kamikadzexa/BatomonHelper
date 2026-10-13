param([string]$Python = "python", [switch]$Cpu)
$ErrorActionPreference = 'Stop'
$trainingRoot = $PSScriptRoot
& $Python -m venv (Join-Path $trainingRoot '.venv')
if ($LASTEXITCODE -ne 0) { throw 'Python virtual environment creation failed.' }
$trainingPython = Join-Path $trainingRoot '.venv\Scripts\python.exe'
$torchIndex = if ($Cpu) { 'https://download.pytorch.org/whl/cpu' } else { 'https://download.pytorch.org/whl/cu128' }
& $trainingPython -m pip install torch==2.7.1 --index-url $torchIndex
if ($LASTEXITCODE -ne 0) { throw 'PyTorch installation failed.' }
& $trainingPython -m pip install numpy==2.2.6
if ($LASTEXITCODE -ne 0) { throw 'NumPy installation failed.' }
& $trainingPython -c 'import torch; print("PyTorch", torch.__version__, "CUDA available:", torch.cuda.is_available())'
