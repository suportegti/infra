#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Remove o servico RDSMonitor.Web e a regra de firewall.

.PARAMETER InstallDir
    Pasta de instalacao. Padrao: C:\RDSMonitor\Web

.PARAMETER RemoverArquivos
    Apaga o aplicativo, MAS mantem a pasta data\ (banco) e appsettings.Production.json.

.PARAMETER RemoverBanco
    Junto com -RemoverArquivos, apaga tambem o banco de dados e a configuracao.
    Irreversivel: todo o historico de sessoes e usuarios sera perdido.
#>
[CmdletBinding()]
param(
    [string] $InstallDir = "C:\RDSMonitor\Web",
    [switch] $RemoverArquivos,
    [switch] $RemoverBanco
)

$ErrorActionPreference = "Stop"
$ServiceName   = "RDSMonitor.Web"
$RegraFirewall = "RDSMonitor Web (entrada TCP)"

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch { }
    }
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "Servico $ServiceName removido." -ForegroundColor Green
}
else {
    Write-Host "Servico $ServiceName nao esta instalado."
}

Get-NetFirewallRule -DisplayName $RegraFirewall -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host "Regra de firewall removida (se existia)."

if ($RemoverArquivos -and (Test-Path $InstallDir)) {
    Start-Sleep -Seconds 2
    if ($RemoverBanco) {
        Remove-Item -Path $InstallDir -Recurse -Force
        Write-Host "Pasta $InstallDir apagada, incluindo banco e configuracao." -ForegroundColor Green
    }
    else {
        Get-ChildItem -Path $InstallDir -Force |
            Where-Object { $_.Name -ne 'data' -and $_.Name -ne 'appsettings.Production.json' } |
            Remove-Item -Recurse -Force
        Write-Host "Aplicativo removido. Banco (data\) e appsettings.Production.json mantidos em $InstallDir." -ForegroundColor Green
    }
}
