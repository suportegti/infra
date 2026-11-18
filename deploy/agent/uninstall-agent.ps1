#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Remove o servico RDSMonitor.Agent deste servidor.

.PARAMETER InstallDir
    Pasta de instalacao. Padrao: C:\RDSMonitor\Agent

.PARAMETER RemoverArquivos
    Tambem apaga a pasta de instalacao (inclui o appsettings.json com a chave).

.EXAMPLE
    .\uninstall-agent.ps1 -RemoverArquivos
#>
[CmdletBinding()]
param(
    [string] $InstallDir = "C:\RDSMonitor\Agent",
    [switch] $RemoverArquivos
)

$ErrorActionPreference = "Stop"
$ServiceName = "RDSMonitor.Agent"

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

if ($RemoverArquivos -and (Test-Path $InstallDir)) {
    Start-Sleep -Seconds 2
    Remove-Item -Path $InstallDir -Recurse -Force
    Write-Host "Pasta $InstallDir apagada." -ForegroundColor Green
}

Write-Host "O cadastro do servidor no portal NAO foi alterado - desabilite ou exclua em Servidores, se quiser."
