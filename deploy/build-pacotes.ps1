<#
.SYNOPSIS
    Gera os pacotes de instalacao do RDSMonitor (portal e agente) em dist\.

.DESCRIPTION
    Rode na maquina de desenvolvimento (precisa do .NET 8 SDK). Publica os dois
    projetos como "self-contained" para win-x64 (os servidores NAO precisam do
    .NET instalado) e monta:
        dist\RDSMonitor.Web-<versao>.zip    -> app\ + install-web.ps1 + uninstall-web.ps1
        dist\RDSMonitor.Agent-<versao>.zip  -> app\ + install-agent.ps1 + uninstall-agent.ps1

.EXAMPLE
    .\deploy\build-pacotes.ps1
    .\deploy\build-pacotes.ps1 -Versao 1.2.0
#>
[CmdletBinding()]
param(
    [string] $Versao = (Get-Date -Format "yyyy.MM.dd-HHmm")
)

$ErrorActionPreference = "Stop"

$Raiz = Split-Path -Parent $PSScriptRoot
$Dist = Join-Path $Raiz "dist"
$Docs = Join-Path $Raiz "docs\IMPLANTACAO.md"

$pacotes = @(
    @{ Nome = "RDSMonitor.Web";   Projeto = "src\RDSMonitor.Web\RDSMonitor.Web.csproj";     Scripts = "deploy\web" },
    @{ Nome = "RDSMonitor.Agent"; Projeto = "src\RDSMonitor.Agent\RDSMonitor.Agent.csproj"; Scripts = "deploy\agent" }
)

if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
New-Item -ItemType Directory -Path $Dist | Out-Null

foreach ($p in $pacotes) {
    $pasta = Join-Path $Dist $p.Nome
    $app   = Join-Path $pasta "app"
    Write-Host "== Publicando $($p.Nome) ==" -ForegroundColor Cyan

    & dotnet publish (Join-Path $Raiz $p.Projeto) -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=false -p:DebugType=None -p:Version=$($Versao -replace '-.*$','') `
        -o $app --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou para $($p.Nome)." }

    # Nada de configuracao de desenvolvimento, bancos ou simbolos no pacote
    Get-ChildItem $app -Recurse -File -Include "appsettings.Development.json", "*.db", "*.db-*", "*.pdb" |
        Remove-Item -Force

    Copy-Item (Join-Path $Raiz "$($p.Scripts)\*.ps1") $pasta
    if (Test-Path $Docs) { Copy-Item $Docs (Join-Path $pasta "IMPLANTACAO.md") }

    $zip = Join-Path $Dist "$($p.Nome)-$Versao.zip"
    Compress-Archive -Path (Join-Path $pasta "*") -DestinationPath $zip -Force
    $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "   -> $zip ($mb MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Pacotes prontos em $Dist" -ForegroundColor Green
