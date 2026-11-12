#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Instala o RDSMonitor.Agent como Windows Service em um servidor RDS.

.DESCRIPTION
    Este script deve ser executado UMA VEZ em cada servidor RDS com privilégios
    de Administrador. Ele:
      1. Verifica pré-requisitos (.NET 8 Runtime)
      2. Copia os arquivos para C:\RDSMonitor\Agent\
      3. Configura o appsettings.json com os valores informados
      4. Registra e inicia o Windows Service

.PARAMETER ServerName
    Nome deste servidor (deve ser único; ex: "RDS01"). Padrão: hostname atual.

.PARAMETER WebApiUrl
    URL completa do WebServer. Ex: "https://rdsmonitor.empresa.local"

.PARAMETER ApiKey
    Chave de API idêntica à configurada em AgentSettings:ApiKey no WebServer.

.PARAMETER InstallDir
    Diretório de instalação. Padrão: C:\RDSMonitor\Agent

.EXAMPLE
    .\install-agent.ps1 -WebApiUrl "https://rdsmonitor.empresa.local" `
                         -ApiKey "minha-chave-secreta"

.EXAMPLE
    .\install-agent.ps1 -ServerName "RDS03" `
                         -WebApiUrl "https://rdsmonitor.empresa.local" `
                         -ApiKey "minha-chave-secreta" `
                         -InstallDir "D:\Servicos\RDSMonitor"
#>

[CmdletBinding()]
param(
    [string] $ServerName  = $env:COMPUTERNAME,
    [Parameter(Mandatory)] [string] $WebApiUrl,
    [Parameter(Mandatory)] [string] $ApiKey,
    [string] $InstallDir  = "C:\RDSMonitor\Agent"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ServiceName    = "RDSMonitor.Agent"
$ServiceDisplay = "RDSMonitor - Agente RDS"
$ServiceDesc    = "Coleta sessoes, processos e executa comandos do portal RDSManager."
$ExeName        = "RDSMonitor.Agent.exe"
$ExePath        = Join-Path $InstallDir $ExeName

# ── Banner ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║       RDSMonitor.Agent  —  Instalador           ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Servidor  : $ServerName"
Write-Host "  API URL   : $WebApiUrl"
Write-Host "  Destino   : $InstallDir"
Write-Host ""

# ── 1. Verificar .NET 8 Runtime ────────────────────────────────────────────────
Write-Host "[1/5] Verificando .NET 8 Runtime..." -ForegroundColor Yellow

$dotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host "      ERRO: dotnet.exe nao encontrado. Instale o .NET 8 Runtime:" -ForegroundColor Red
    Write-Host "      https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Red
    exit 1
}

$runtimes = & dotnet --list-runtimes 2>&1
$hasNet8  = $runtimes | Where-Object { $_ -match "Microsoft\.NETCore\.App 8\." }
if (-not $hasNet8) {
    Write-Host "      AVISO: .NET 8 Runtime nao encontrado. Certifique-se de instalar:" -ForegroundColor Yellow
    Write-Host "      https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
    $confirm = Read-Host "      Continuar mesmo assim? (S/N)"
    if ($confirm -ne "S") { exit 1 }
}
else {
    Write-Host "      OK — .NET 8 encontrado." -ForegroundColor Green
}

# ── 2. Parar serviço existente (se houver) ────────────────────────────────────
Write-Host "[2/5] Verificando servico existente..." -ForegroundColor Yellow

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "      Servico encontrado (Status: $($existing.Status)). Parando..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    # Remove o serviço antigo para reinstalar limpo
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "      Servico antigo removido." -ForegroundColor Green
}
else {
    Write-Host "      Nenhum servico anterior encontrado." -ForegroundColor Green
}

# ── 3. Copiar arquivos ────────────────────────────────────────────────────────
Write-Host "[3/5] Copiando arquivos para $InstallDir..." -ForegroundColor Yellow

# Origem: pasta do script (o zip do agente deve ser extraído na mesma pasta)
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

# Copia tudo da pasta atual para o destino
Copy-Item -Path "$ScriptDir\*" -Destination $InstallDir -Recurse -Force -Exclude "install-agent.ps1"

if (-not (Test-Path $ExePath)) {
    Write-Host "      ERRO: $ExeName nao encontrado em $InstallDir" -ForegroundColor Red
    Write-Host "      Certifique-se de publicar o projeto antes de instalar:" -ForegroundColor Red
    Write-Host "      dotnet publish -c Release -r win-x64 --self-contained false" -ForegroundColor Red
    exit 1
}

Write-Host "      Arquivos copiados." -ForegroundColor Green

# ── 4. Configurar appsettings.json ────────────────────────────────────────────
Write-Host "[4/5] Configurando appsettings.json..." -ForegroundColor Yellow

$settingsPath = Join-Path $InstallDir "appsettings.json"
$settings = @{
    AgentSettings = @{
        ServerName              = $ServerName
        WebApiUrl               = $WebApiUrl
        ApiKey                  = $ApiKey
        HeartbeatIntervalSeconds = 30
        CommandPollIntervalSeconds = 10
    }
    Logging = @{
        LogLevel = @{
            Default                      = "Information"
            "Microsoft.Hosting.Lifetime" = "Information"
        }
        EventLog = @{
            LogLevel = @{ Default = "Information" }
        }
    }
}

$settings | ConvertTo-Json -Depth 5 | Set-Content -Path $settingsPath -Encoding UTF8
Write-Host "      appsettings.json gravado em $settingsPath" -ForegroundColor Green

# ── 5. Registrar e iniciar o serviço Windows ─────────────────────────────────
Write-Host "[5/5] Registrando Windows Service..." -ForegroundColor Yellow

$result = & sc.exe create $ServiceName `
    binPath= "`"$ExePath`"" `
    DisplayName= "$ServiceDisplay" `
    start= auto `
    obj= LocalSystem

if ($LASTEXITCODE -ne 0) {
    Write-Host "      ERRO ao criar servico: $result" -ForegroundColor Red
    exit 1
}

& sc.exe description $ServiceName "$ServiceDesc" | Out-Null

# Configura reinício automático em caso de falha
& sc.exe failure $ServiceName reset= 3600 actions= restart/5000/restart/10000/restart/30000 | Out-Null

Write-Host "      Iniciando servico..." -ForegroundColor Yellow
Start-Service -Name $ServiceName
Start-Sleep -Seconds 3

$svc = Get-Service -Name $ServiceName
if ($svc.Status -eq "Running") {
    Write-Host "      Servico '$ServiceName' em execucao." -ForegroundColor Green
}
else {
    Write-Host "      AVISO: Servico nao iniciou corretamente (Status: $($svc.Status))" -ForegroundColor Yellow
    Write-Host "      Verifique o Log de Eventos do Windows para detalhes." -ForegroundColor Yellow
}

# ── Resumo ────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Instalacao concluida!" -ForegroundColor Green
Write-Host ""
Write-Host "  Comandos uteis:"
Write-Host "    Get-Service $ServiceName           # ver status"
Write-Host "    Stop-Service $ServiceName          # parar"
Write-Host "    Start-Service $ServiceName         # iniciar"
Write-Host "    Restart-Service $ServiceName       # reiniciar"
Write-Host "    sc.exe delete $ServiceName         # desinstalar"
Write-Host ""
Write-Host "  Logs: Visualizador de Eventos > Logs do Windows > Aplicativo"
Write-Host "  Fonte: RDSMonitor.Agent"
Write-Host "══════════════════════════════════════════════════" -ForegroundColor Cyan
