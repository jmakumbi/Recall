#Requires -Version 5.1
<#
.SYNOPSIS
    Recall installer — bootstraps all prerequisites and installs the recall CLI.

.DESCRIPTION
    1. Checks for / installs Ollama
    2. Pulls the required embedding and chat models
    3. Verifies that libs\vec0.dll and libs\Everything64.dll are present
    4. Creates %APPDATA%\recall\ and places recall.exe + dependencies there
    5. Adds %APPDATA%\recall to the user PATH

.NOTES
    Run as a normal user (no elevation required).
    To install from the release zip:
        Expand-Archive recall-v*.zip .
        .\install.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $EmbeddingModel = "nomic-embed-text",
    [string] $ChatModel      = "qwen3:8b",
    [switch] $SkipOllama,
    [switch] $SkipModelPull
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$InstallDir = Join-Path $env:APPDATA "recall"
$ScriptDir  = $PSScriptRoot

# ── Helpers ───────────────────────────────────────────────────────────────────

function Write-Step   { param($msg) Write-Host "  ► $msg" -ForegroundColor Cyan }
function Write-Ok     { param($msg) Write-Host "    ✓ $msg" -ForegroundColor Green }
function Write-Warn   { param($msg) Write-Host "    ⚠ $msg" -ForegroundColor Yellow }
function Write-Fail   { param($msg) Write-Host "    ✗ $msg" -ForegroundColor Red }

function Add-ToUserPath {
    param([string]$Dir)
    $current = [Environment]::GetEnvironmentVariable("PATH", "User")
    if ($current -notlike "*$Dir*") {
        [Environment]::SetEnvironmentVariable("PATH", "$current;$Dir", "User")
        $env:PATH += ";$Dir"
        Write-Ok "Added $Dir to user PATH"
    } else {
        Write-Ok "$Dir already in user PATH"
    }
}

# ── Banner ────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "  ██████╗ ███████╗ ██████╗ █████╗ ██╗     ██╗" -ForegroundColor Cyan
Write-Host "  ██╔══██╗██╔════╝██╔════╝██╔══██╗██║     ██║" -ForegroundColor Cyan
Write-Host "  ██████╔╝█████╗  ██║     ███████║██║     ██║" -ForegroundColor Cyan
Write-Host "  ██╔══██╗██╔══╝  ██║     ██╔══██║██║     ██║" -ForegroundColor Cyan
Write-Host "  ██║  ██║███████╗╚██████╗██║  ██║███████╗███████╗" -ForegroundColor Cyan
Write-Host "  ╚═╝  ╚═╝╚══════╝ ╚═════╝╚═╝  ╚═╝╚══════╝╚══════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Personal Knowledge Base — powered by local Ollama LLMs" -ForegroundColor White
Write-Host ""

# ── 1. Ollama ─────────────────────────────────────────────────────────────────

Write-Step "Checking Ollama…"

$ollamaCmd = Get-Command "ollama" -ErrorAction SilentlyContinue

if ($ollamaCmd) {
    Write-Ok "Ollama found at $($ollamaCmd.Source)"
} elseif ($SkipOllama) {
    Write-Warn "Ollama not found — skipped (--SkipOllama)"
} else {
    Write-Step "Ollama not found — downloading installer…"
    $ollamaInstaller = Join-Path $env:TEMP "OllamaSetup.exe"
    try {
        Invoke-WebRequest "https://ollama.com/download/windows" -OutFile $ollamaInstaller -UseBasicParsing
        Write-Step "Running Ollama installer (silent)…"
        Start-Process -FilePath $ollamaInstaller -ArgumentList "/S" -Wait
        Write-Ok "Ollama installed"
    } catch {
        Write-Fail "Failed to download/install Ollama: $_"
        Write-Warn "Install Ollama manually from https://ollama.com/download and re-run this script."
        exit 1
    }
}

# ── 2. Model pull ─────────────────────────────────────────────────────────────

if (-not $SkipModelPull) {
    foreach ($model in @($EmbeddingModel, $ChatModel)) {
        Write-Step "Pulling $model…"
        try {
            & ollama pull $model
            if ($LASTEXITCODE -ne 0) { throw "ollama pull exited with code $LASTEXITCODE" }
            Write-Ok "$model ready"
        } catch {
            Write-Warn "Could not pull ${model}: $_"
            Write-Warn "Run manually: ollama pull $model"
        }
    }
} else {
    Write-Warn "Model pull skipped (--SkipModelPull)"
}

# ── 3. Native DLLs ────────────────────────────────────────────────────────────

Write-Step "Checking native libraries…"

$libsDir    = Join-Path $ScriptDir "libs"
$vec0Path   = Join-Path $libsDir "vec0.dll"
$evthPath   = Join-Path $libsDir "Everything64.dll"

if (-not (Test-Path $vec0Path)) {
    Write-Fail "vec0.dll not found at $vec0Path"
    Write-Warn @"
Download vec0.dll from:
  https://github.com/asg017/sqlite-vec/releases
Look for: sqlite-vec-*-loadable-windows-x86_64.tar.gz
Extract vec0.dll → place it in libs\vec0.dll next to this script.
"@
    exit 1
} else {
    Write-Ok "vec0.dll present"
}

if (-not (Test-Path $evthPath)) {
    Write-Warn "Everything64.dll not found at $evthPath"
    Write-Warn @"
For Everything-powered fast search (optional):
  1. Install Everything from https://www.voidtools.com
  2. Copy Everything64.dll from the Everything SDK to libs\Everything64.dll
recall will fall back to Windows Search (WDS) if Everything is not available.
"@
} else {
    Write-Ok "Everything64.dll present"
}

# ── 4. Install files ──────────────────────────────────────────────────────────

Write-Step "Installing to $InstallDir…"

if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

# Copy the executable and its dependencies
$sourceItems = @(
    (Join-Path $ScriptDir "recall.exe"),
    (Join-Path $ScriptDir "appsettings.json")
)

$missingExe = $sourceItems | Where-Object { -not (Test-Path $_) }
if ($missingExe) {
    Write-Fail "Missing files: $($missingExe -join ', ')"
    Write-Warn "Build recall first: dotnet publish Recall.Cli -c Release -r win-x64"
    exit 1
}

foreach ($item in $sourceItems) {
    Copy-Item $item -Destination $InstallDir -Force
}

# Copy all DLLs from the publish directory
Get-ChildItem $ScriptDir -Filter "*.dll" | Copy-Item -Destination $InstallDir -Force

# Copy libs directory
$destLibs = Join-Path $InstallDir "libs"
if (Test-Path $libsDir) {
    if (-not (Test-Path $destLibs)) {
        New-Item -ItemType Directory -Path $destLibs -Force | Out-Null
    }
    Copy-Item (Join-Path $libsDir "*") -Destination $destLibs -Force
}

Write-Ok "Files installed to $InstallDir"

# ── 5. PATH ───────────────────────────────────────────────────────────────────

Write-Step "Configuring user PATH…"
Add-ToUserPath $InstallDir

# ── Done ──────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "  Installation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "  Start a new terminal and run:" -ForegroundColor White
Write-Host "    recall" -ForegroundColor Cyan
Write-Host ""
Write-Host "  First-time setup:" -ForegroundColor White
Write-Host "    recall> /setup    # configure which folders to search" -ForegroundColor Cyan
Write-Host ""
