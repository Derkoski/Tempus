<#
.SYNOPSIS
    Remove o Tempus instalado para o usuário atual.

.PARAMETER KeepData
    Preserva %APPDATA%\Tempus (client_secret.json e o token). Padrão: preserva.

.PARAMETER PurgeData
    Apaga também os dados. Use ao sair de uma máquina de vez.
#>
[CmdletBinding()]
param([switch]$PurgeData)

$ErrorActionPreference = 'Stop'

Get-Process Tempus -ErrorAction SilentlyContinue | ForEach-Object {
    $_ | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

$targets = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Tempus.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Tempus.lnk'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Tempus')
)

foreach ($t in $targets) {
    if (Test-Path $t) {
        Remove-Item $t -Recurse -Force
        Write-Host "removido: $t" -ForegroundColor DarkGray
    }
}

$data = Join-Path $env:APPDATA 'Tempus'
if ($PurgeData) {
    if (Test-Path $data) {
        Remove-Item $data -Recurse -Force
        Write-Host "removido: $data" -ForegroundColor DarkGray
    }
} elseif (Test-Path $data) {
    # Apagar por padrao custaria um novo consent no Google sem que ninguem tenha pedido isso.
    Write-Host "preservado: $data  (use -PurgeData para apagar credenciais)" -ForegroundColor DarkGray
}

Write-Host 'Tempus desinstalado.' -ForegroundColor Green
