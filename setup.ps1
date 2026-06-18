#Requires -Version 5.1
<#
.SYNOPSIS
  Prerequisite check + first-run setup for the PAS -> Secret Server migration platform on Windows.
  Requires Docker Desktop with the WSL2 or Hyper-V backend.

.PARAMETER NoAi
  Skip starting the local Ollama AI containers.

.PARAMETER Foreground
  Run docker compose in the foreground (stream logs) instead of detached.

.EXAMPLE
  .\setup.ps1
  .\setup.ps1 -NoAi
#>
[CmdletBinding()]
param(
    [switch]$NoAi,
    [switch]$Foreground
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "[ok]   $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "[warn] $msg" -ForegroundColor Yellow }
function Write-Err2($msg) { Write-Host "[fail] $msg" -ForegroundColor Red }

Set-Location -Path $PSScriptRoot

# ---------- 1. Docker present? ----------
Write-Step "Checking Docker"
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Err2 "Docker is not installed or not on PATH."
    Write-Host "    Install Docker Desktop for Windows: https://docs.docker.com/desktop/install/windows-install/"
    Write-Host "    After install, launch Docker Desktop and wait until it reports 'running'."
    exit 1
}
Write-Ok ("docker found: " + (docker --version))

# ---------- 2. Docker daemon reachable? ----------
docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Err2 "The Docker daemon isn't reachable."
    Write-Host "    Start Docker Desktop and wait for it to finish starting, then re-run this script."
    exit 1
}
Write-Ok "docker daemon is running"

# ---------- 3. Compose v2 plugin? ----------
Write-Step "Checking Docker Compose"
docker compose version *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Err2 "The Docker Compose v2 plugin is missing."
    Write-Host "    It ships with Docker Desktop - update Docker Desktop to the latest version."
    exit 1
}
Write-Ok ("compose found: " + ((docker compose version) | Select-Object -First 1))

# ---------- 4. RAM advisory ----------
Write-Step "Checking host resources"
try {
    $totalGb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
    if (-not $NoAi -and $totalGb -lt 8) {
        Write-Warn2 "Detected ${totalGb}GB RAM. The local AI model needs ~5-6GB free."
        Write-Warn2 "Consider a smaller model (set OLLAMA_CHAT_MODEL=llama3.2:3b in .env) or run with -NoAi."
        Write-Warn2 "Note: Docker Desktop's WSL2 backend has its own memory limit - check Settings > Resources."
    } else {
        Write-Ok "detected ${totalGb}GB RAM"
    }
} catch {
    Write-Warn2 "Could not determine total RAM; continuing."
}

# ---------- 4b. Port availability ----------
Write-Step "Checking host ports"
function Test-PortInUse([int]$Port) {
    try {
        $conns = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop
        return ($null -ne $conns)
    } catch { return $false }
}
$ports = @(5432, 8080, 5173)
if (-not $NoAi) { $ports += 11434 }
$clash = $false
foreach ($p in $ports) {
    if (Test-PortInUse $p) {
        $clash = $true
        if ($p -eq 11434) {
            Write-Err2 "Port 11434 is already in use - most likely a native Ollama service."
            Write-Host "    Quit the Ollama app / stop the service so the container can bind it,"
            Write-Host "    then re-run this script. (Or run with -NoAi to skip the AI containers.)"
        } else {
            Write-Err2 "Port $p is already in use by another process."
            Write-Host "    Stop whatever is using it, or change the host port in docker-compose.yml."
        }
    }
}
if ($clash) {
    Write-Err2 "Resolve the port conflict(s) above, then run .\setup.ps1 again."
    exit 1
}
Write-Ok "required host ports are free"

# ---------- 5. .env ----------
Write-Step "Preparing environment file (.env)"
if (Test-Path ".env") {
    Write-Ok ".env already exists - leaving it untouched"
} else {
    Copy-Item ".env.example" ".env"
    # Generate a strong DB password.
    $bytes = New-Object byte[] 18
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $genpw = ([Convert]::ToBase64String($bytes) -replace '[/+=]', '').Substring(0, 18)
    (Get-Content ".env") -replace '^POSTGRES_PASSWORD=.*', "POSTGRES_PASSWORD=$genpw" |
        Set-Content ".env" -Encoding UTF8
    Write-Ok "created .env with a generated database password"
}

# ---------- 6. Bring the stack up ----------
Write-Step "Building and starting containers"
$detach = if ($Foreground) { @() } else { @("-d") }

if ($NoAi) {
    Write-Ok "skipping AI containers (-NoAi)"
    docker compose up --build @detach db api frontend
} else {
    Write-Ok "starting full stack incl. local AI (first run pulls the model - this can take several minutes)"
    docker compose up --build @detach
}

if (-not $Foreground) {
    Write-Host ""
    Write-Ok "Stack is starting in the background."
    Write-Host "    Frontend:    http://localhost:5173"
    Write-Host "    API health:  http://localhost:8080/health/ready"
    Write-Host "    Logs:        docker compose logs -f"
    Write-Host "    Stop:        docker compose down        (keeps data)"
    Write-Host "    Reset DB:    docker compose down -v     (wipes the database volume)"
}
