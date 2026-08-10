<#
    Biosphere -> GitHub, one shot.

    Run this from PowerShell in the biosphere folder:

        cd $HOME\Desktop\biosphere
        .\push_to_github.ps1 -RepoName biosphere

    If PowerShell blocks it ("running scripts is disabled"), either run
        powershell -ExecutionPolicy Bypass -File .\push_to_github.ps1 -RepoName biosphere
    or just follow the manual commands printed at the bottom of this file.

    Prefers the GitHub CLI (`gh`) because it handles auth, repo creation and
    the remote in one step. Falls back to plain git if gh isn't installed.
#>

param(
    [string]$RepoName = "biosphere",
    [ValidateSet("public", "private")]
    [string]$Visibility = "private",
    [string]$Branch = "main"
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

function Say($msg)  { Write-Host "  $msg" -ForegroundColor Cyan }
function Warn($msg) { Write-Host "  $msg" -ForegroundColor Yellow }
function Die($msg)  { Write-Host "  $msg" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "Biosphere -> GitHub" -ForegroundColor White
Write-Host "-------------------"

# ---- Preflight ----------------------------------------------------------
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Die "git is not installed. Get it from https://git-scm.com/download/win then re-run."
}
if (-not (Test-Path ".gitignore")) {
    Die "No .gitignore here. Are you running this from the biosphere folder?"
}

# A partial .git may exist from an earlier attempt (including a stale
# index.lock). Starting clean is more predictable than repairing it.
if (Test-Path ".git") {
    Warn "Existing .git found - removing it and starting fresh."
    Remove-Item -Recurse -Force ".git"
}

# ---- Identity -----------------------------------------------------------
$existingName  = (git config --global user.name)  2>$null
$existingEmail = (git config --global user.email) 2>$null

git init --quiet
git branch -M $Branch

if (-not $existingName)  { git config user.name  "Abiram" }
if (-not $existingEmail) { git config user.email "abiramr799@gmail.com" }

# Unity assets are binary; normalising line endings on them corrupts files.
git config core.autocrlf false

# ---- Commit -------------------------------------------------------------
Say "Staging files (venv, __pycache__ and *.csv are excluded by .gitignore)..."
git add -A

$fileCount = (git diff --cached --name-only | Measure-Object -Line).Lines
Say "$fileCount files staged."

if ($fileCount -eq 0) { Die "Nothing to commit. Something is wrong with .gitignore." }

$msg = @"
Biosphere: numpy evolution prototype + Unity 2D WorldBox-style port

Python prototype (repo root): cells with 5-trait heritable genomes on a
procedurally generated island. Selection emerges from world mechanics
(sunlight, cloud shadow, weather, terrain) rather than being scripted.

Unity port (BiosphereUnity/): same simulation, built for scale. Terrain is
one point-filtered Texture2D with dirty-rect repaint rather than a Tilemap;
all sprites draw through GPU-instanced batches with depth-buffer Y sorting;
particles are a fixed Burst-simulated pool. ~5 draw calls for the whole
world regardless of entity count.

Unity code is syntax-checked only and has never been compiled.
See BiosphereUnity/ARCHITECTURE.md section 9.
"@

git commit --quiet -m $msg
Say "Committed."

# ---- Push ---------------------------------------------------------------
if (Get-Command gh -ErrorAction SilentlyContinue) {
    Say "GitHub CLI found."

    gh auth status *> $null
    if ($LASTEXITCODE -ne 0) {
        Say "Not logged in - opening browser login..."
        gh auth login --web --git-protocol https
        if ($LASTEXITCODE -ne 0) { Die "GitHub login failed." }
    }

    Say "Creating repo '$RepoName' ($Visibility) and pushing..."
    gh repo create $RepoName --$Visibility --source=. --remote=origin --push
    if ($LASTEXITCODE -ne 0) {
        Die "gh repo create failed. If the name is taken, re-run with -RepoName something-else."
    }

    $url = (gh repo view --json url --jq .url)
    Write-Host ""
    Write-Host "  Done: $url" -ForegroundColor Green
}
else {
    Write-Host ""
    Warn "GitHub CLI (gh) not installed - can't create the repo automatically."
    Warn "Easiest fix: install it, then re-run this script:"
    Write-Host "      winget install --id GitHub.cli" -ForegroundColor White
    Write-Host ""
    Warn "Or do the last two steps by hand:"
    Write-Host "   1. Create an EMPTY repo at https://github.com/new" -ForegroundColor White
    Write-Host "      (no README, no .gitignore, no licence - this repo already has them)" -ForegroundColor DarkGray
    Write-Host "   2. Then run:" -ForegroundColor White
    Write-Host ""
    Write-Host "      git remote add origin https://github.com/<your-username>/$RepoName.git" -ForegroundColor White
    Write-Host "      git push -u origin $Branch" -ForegroundColor White
    Write-Host ""
    Say "The commit is already made, so you only need those two lines."
}
