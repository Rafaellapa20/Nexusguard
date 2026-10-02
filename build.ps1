<#
.SYNOPSIS
    Compila o NexusGuard e, se o Inno Setup estiver instalado, gera também o instalador.

.EXAMPLE
    .\build.ps1                      # executável único, precisa do .NET 10 instalado
    .\build.ps1 -SelfContained       # inclui o .NET no executável (recomendado para distribuir)
    .\build.ps1 -SelfContained -Installer   # + NexusGuard-Setup-1.0.0.exe
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$Installer,
    [string]$Runtime = 'win-x64',
    [string]$Output
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Output) { $Output = Join-Path $root 'dist' }

$project = Join-Path $root 'src\NexusGuard\NexusGuard.csproj'

if (-not (Test-Path $project)) {
    throw "Projeto não encontrado em $project"
}

Write-Host 'Compilando o NexusGuard...' -ForegroundColor Cyan

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $Output,
    '--nologo',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '--self-contained'
)

if ($SelfContained) { $publishArgs += 'true' } else { $publishArgs += 'false' }

& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "A compilação falhou (código $LASTEXITCODE)."
}

$exe = Join-Path $Output 'NexusGuard.exe'

Write-Host ''
Write-Host 'Aplicativo compilado.' -ForegroundColor Green
Write-Host "  $exe"

if (-not $Installer) {
    Write-Host ''
    Write-Host 'Para gerar o instalador: .\build.ps1 -SelfContained -Installer'
    return
}

# ----------------------------------------------------------------- Instalador
$iscc = $null

foreach ($candidate in @(
    "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)) {
    if (Test-Path $candidate) { $iscc = $candidate; break }
}

if (-not $iscc) {
    $cmd = Get-Command 'iscc.exe' -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}

if (-not $iscc) {
    Write-Warning 'Inno Setup 6 não encontrado — o instalador não foi gerado.'
    Write-Host 'Instale com:  winget install JRSoftware.InnoSetup'
    return
}

Write-Host ''
Write-Host 'Gerando o instalador...' -ForegroundColor Cyan

& $iscc (Join-Path $root 'installer\NexusGuard.iss')

if ($LASTEXITCODE -ne 0) {
    throw "O Inno Setup falhou (código $LASTEXITCODE)."
}

$setup = Get-ChildItem -Path $Output -Filter 'NexusGuard-Setup-*.exe' |
         Sort-Object LastWriteTime -Descending |
         Select-Object -First 1

Write-Host ''
Write-Host 'Instalador gerado.' -ForegroundColor Green
if ($setup) { Write-Host "  $($setup.FullName)" }

Write-Host ''
Write-Host 'Nota: sem um certificado de assinatura de código, o SmartScreen vai avisar os'
Write-Host 'primeiros usuários. A assinatura tem de ser feita com um certificado próprio.'
