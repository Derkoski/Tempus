<#
.SYNOPSIS
    Instala o Tempus para o usuário atual, com atalho no Menu Iniciar e início automático.

.DESCRIPTION
    Copia o executável autocontido para %LOCALAPPDATA%\Programs\Tempus e cria os atalhos.
    Não exige administrador: tudo acontece dentro do perfil do usuário.

    Rodar de dentro do repositório é o que NÃO se deve fazer no dia a dia — 'bin' é apagado a cada
    rebuild e 'publish' é sobrescrito. Daí existir um destino estável.

.PARAMETER Publish
    Gera o pacote antes de instalar (precisa do SDK do .NET). Sem isto, usa 'publish' como está.

.PARAMETER NoStartup
    Não inicia junto com o Windows.

.EXAMPLE
    .\scripts\install.ps1 -Publish
#>
[CmdletBinding()]
param(
    [switch]$Publish,
    [switch]$NoStartup,
    [string]$Source
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Source)) { $Source = Join-Path $repo 'publish' }

if ($Publish) {
    Write-Host 'Gerando pacote autocontido...' -ForegroundColor Cyan
    & dotnet publish (Join-Path $repo 'Tempus\Tempus.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o $Source
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou.' }
}

$sourceExe = Join-Path $Source 'Tempus.exe'
if (-not (Test-Path $sourceExe)) {
    throw "Nao achei '$sourceExe'. Rode com -Publish para gerar o pacote antes."
}

$dest = Join-Path $env:LOCALAPPDATA 'Programs\Tempus'
$destExe = Join-Path $dest 'Tempus.exe'
$destSettings = Join-Path $dest 'appsettings.json'

# Substituir o exe com ele em execucao falha; encerrar antes evita um erro obscuro.
Get-Process Tempus -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host 'Encerrando instancia em execucao...' -ForegroundColor DarkGray
    $_ | Stop-Process -Force
    Start-Sleep -Milliseconds 600
}

if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }

Write-Host "Instalando em $dest" -ForegroundColor Cyan
Copy-Item $sourceExe $destExe -Force

# A configuracao existente e preservada: ela carrega LoginHint, horarios de expediente e a
# consulta de e-mail. Sobrescrever numa reinstalacao apagaria ajustes feitos a mao.
if (Test-Path $destSettings) {
    Write-Host '  appsettings.json existente preservado' -ForegroundColor DarkGray
} else {
    $sourceSettings = Join-Path $Source 'appsettings.json'
    if (Test-Path $sourceSettings) { Copy-Item $sourceSettings $destSettings }
}

# Remove a marca de "veio da internet", que faz o SmartScreen avisar de novo.
try { Unblock-File $destExe -ErrorAction Stop } catch {}

function New-Shortcut([string]$Path, [string]$Target, [string]$Description) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.WorkingDirectory = Split-Path -Parent $Target
    $link.Description = $Description
    $link.Save()
}

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Tempus.lnk'
New-Shortcut $startMenu $destExe 'Tempus - barra de agenda e tarefas'
Write-Host '  atalho no Menu Iniciar criado' -ForegroundColor DarkGray

$startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Tempus.lnk'
if ($NoStartup) {
    if (Test-Path $startup) { Remove-Item $startup -Force }
    Write-Host '  inicio automatico desativado' -ForegroundColor DarkGray
} else {
    New-Shortcut $startup $destExe 'Tempus'
    Write-Host '  inicia junto com o Windows' -ForegroundColor DarkGray
}

Start-Process $destExe

Write-Host ''
Write-Host 'Pronto.' -ForegroundColor Green
Write-Host "  Executavel : $destExe"
Write-Host "  Config     : $destSettings"
Write-Host "  Dados      : $(Join-Path $env:APPDATA 'Tempus')  (client_secret.json e tokens)"
Write-Host ''
Write-Host 'Procure por "Tempus" no menu Iniciar. Para desinstalar: scripts\uninstall.ps1'
