<#
.SYNOPSIS
    Build + packaging (Velopack) dell'app TIA Var Analyzer, con upload opzionale su GitHub Releases.

.DESCRIPTION
    La build NON può girare su GitHub Actions: i runner non hanno la DLL Openness V18.
    Quindi build e release si fanno IN LOCALE, su una macchina con TIA Portal V18 installato.

    Passi:
      1) dotnet publish (Release)
      2) vpk pack       -> genera Setup.exe + pacchetti in app\Releases\
      3) vpk upload     -> (opzionale) pubblica su GitHub Releases della repo pubblica

.PARAMETER Version
    Versione da pubblicare. Se omessa usa <Version> del csproj.

.PARAMETER Upload
    Se presente, carica la release su GitHub (richiede -Token o env GITHUB_TOKEN).

.PARAMETER Token
    Token GitHub con permesso di scrittura sulle Release. Default: $env:GITHUB_TOKEN.

.EXAMPLE
    .\build-release.ps1                       # build + pack locale
    .\build-release.ps1 -Upload -Token ghp_x  # build + pack + pubblica
#>
[CmdletBinding()]
param(
    [string] $Version = "",
    [switch] $Upload,
    [string] $Token = $env:GITHUB_TOKEN
)

$ErrorActionPreference = 'Stop'
$RepoUrl = 'https://github.com/lucacalcabrini/TiaProgramSerch'
$proj    = Join-Path $PSScriptRoot 'TiaVarAnalyzer\TiaVarAnalyzer.csproj'
$pubDir  = Join-Path $PSScriptRoot 'publish'
$relDir  = Join-Path $PSScriptRoot 'Releases'

# 1) versione
if (-not $Version) {
    [xml]$x = Get-Content $proj
    $Version = @($x.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}
Write-Host "==> Versione: $Version" -ForegroundColor Cyan

# 2) vpk (Velopack CLI) disponibile?
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "==> Installo la CLI Velopack (vpk)..." -ForegroundColor Yellow
    dotnet tool install -g vpk
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
}

# 3) publish
Write-Host "==> dotnet publish..." -ForegroundColor Cyan
if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force }
dotnet publish $proj -c Release -o $pubDir

# 4) pack Velopack -> Setup.exe + pacchetti delta in app\Releases\
Write-Host "==> vpk pack..." -ForegroundColor Cyan
vpk pack `
    --packId      TiaVarAnalyzer `
    --packVersion $Version `
    --packDir     $pubDir `
    --mainExe     TiaVarAnalyzer.exe `
    --packTitle   "TIA Var Analyzer" `
    --outputDir   $relDir

# 5) upload opzionale su GitHub Releases (repo pubblica -> auto-update Velopack all'avvio)
if ($Upload) {
    if (-not $Token) { throw "Serve un token GitHub: usa -Token <tok> oppure imposta `$env:GITHUB_TOKEN." }
    Write-Host "==> vpk upload github..." -ForegroundColor Cyan
    vpk upload github `
        --repoUrl     $RepoUrl `
        --token       $Token `
        --outputDir   $relDir `
        --tag         "app-v$Version" `
        --releaseName "TIA Var Analyzer App v$Version" `
        --merge `
        --publish
}

Write-Host "`nFatto. Output in: $relDir" -ForegroundColor Green
Write-Host "  Setup.exe = installer da distribuire (abilita l'auto-update Velopack)." -ForegroundColor Green
