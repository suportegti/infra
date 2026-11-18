#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Instala (ou atualiza) o RDSMonitor.Agent como servico do Windows em um
    servidor RDS (Windows Server 2016/2019/2022/2025).

.DESCRIPTION
    Execute na pasta extraida do pacote RDSMonitor.Agent-*.zip, em um PowerShell
    aberto como Administrador. O script:
      1. Monta a URL do portal a partir do IP/DNS informado
      2. Testa a comunicacao com o portal (porta, chave da API e cadastro)
      3. Para e remove uma instalacao anterior do servico
      4. Copia os arquivos para a pasta de instalacao
      5. Grava o appsettings.json (com acesso restrito a Administradores/SYSTEM)
      6. Registra o servico (inicio automatico, conta LocalSystem, reinicio em falha)
      7. Inicia o servico e confere se ficou em execucao

    O pacote e "self-contained": NAO e preciso instalar o .NET no servidor.

.PARAMETER ServidorWeb
    Endereco do portal: IP (10.0.0.20), nome DNS (rdsmonitor.lab.local) ou a
    URL completa (http://rdsmonitor.lab.local:5000). Se omitido, sera perguntado.

.PARAMETER Porta
    Porta do portal quando ServidorWeb nao for uma URL. Padrao: 5000.

.PARAMETER Https
    Usa https:// em vez de http:// quando ServidorWeb nao for uma URL.

.PARAMETER ApiKey
    Chave da API, igual a AgentSettings:ApiKey do portal (o instalador do
    portal mostra a chave no final). Se omitida, sera perguntada.

.PARAMETER NomeServidor
    Nome com que este servidor esta (ou sera) cadastrado no portal, na tela
    Servidores. Padrao: nome do computador.

.PARAMETER InstallDir
    Pasta de instalacao. Padrao: C:\RDSMonitor\Agent

.PARAMETER IgnorarCertificado
    Aceita certificado HTTPS auto-assinado no portal. Somente para teste.

.PARAMETER Forcar
    Continua a instalacao mesmo que o teste de comunicacao falhe.

.EXAMPLE
    .\install-agent.ps1 -ServidorWeb 10.0.0.20 -ApiKey "chave-do-portal"

.EXAMPLE
    .\install-agent.ps1 -ServidorWeb rdsmonitor.lab.local -Porta 8080 -NomeServidor RDS-TESTE01

.EXAMPLE
    .\install-agent.ps1 -ServidorWeb https://rdsmonitor.lab.local -IgnorarCertificado
#>

[CmdletBinding()]
param(
    [string] $ServidorWeb,
    [int]    $Porta = 5000,
    [switch] $Https,
    [string] $ApiKey,
    [string] $NomeServidor = $env:COMPUTERNAME,
    [string] $InstallDir = "C:\RDSMonitor\Agent",
    [int]    $HeartbeatSegundos = 30,
    [int]    $ComandosSegundos = 3,
    [switch] $IgnorarCertificado,
    [switch] $Forcar
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ServiceName    = "RDSMonitor.Agent"
$ServiceDisplay = "RDSMonitor - Agente RDS"
$ServiceDesc    = "Coleta sessoes e processos dos usuarios e executa os comandos do portal RDSMonitor."
$ExeName        = "RDSMonitor.Agent.exe"
$ScriptDir      = Split-Path -Parent $MyInvocation.MyCommand.Path
$OrigemApp      = Join-Path $ScriptDir "app"

function Passo($n, $texto) { Write-Host ""; Write-Host "[$n/7] $texto" -ForegroundColor Yellow }
function Ok($texto)        { Write-Host "      OK - $texto" -ForegroundColor Green }
function Aviso($texto)     { Write-Host "      AVISO - $texto" -ForegroundColor DarkYellow }
function Falha($texto)     { Write-Host "      ERRO - $texto" -ForegroundColor Red }

Write-Host ""
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "   RDSMonitor.Agent - Instalador do servico"        -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# -- Pre-requisitos do pacote ------------------------------------------------
if (-not (Test-Path (Join-Path $OrigemApp $ExeName))) {
    Falha "Pasta 'app' com $ExeName nao encontrada ao lado deste script ($OrigemApp)."
    Write-Host "      Extraia o pacote RDSMonitor.Agent-*.zip inteiro e rode o script de dentro dele."
    exit 1
}

# -- Entradas interativas -------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($ServidorWeb)) {
    $ServidorWeb = Read-Host "IP, nome DNS ou URL do portal RDSMonitor (ex.: 10.0.0.20 ou rdsmonitor.lab.local)"
}
if ([string]::IsNullOrWhiteSpace($ServidorWeb)) { Falha "Endereco do portal nao informado."; exit 1 }

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $ApiKey = Read-Host "Chave da API do portal (AgentSettings:ApiKey)"
}
if ([string]::IsNullOrWhiteSpace($ApiKey)) { Falha "Chave da API nao informada."; exit 1 }
$ApiKey = $ApiKey.Trim()

if ($NomeServidor -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$') {
    Falha "Nome de servidor invalido: '$NomeServidor' (use letras, numeros, ponto, hifen e sublinhado)."
    exit 1
}

# -- 1. URL do portal ------------------------------------------------------------
Passo 1 "Montando a URL do portal..."

$entrada = $ServidorWeb.Trim().TrimEnd('/')
if ($entrada -match '^[a-zA-Z][a-zA-Z0-9+.-]*://') {
    $WebApiUrl = $entrada
}
else {
    $esquema = "http"
    if ($Https) { $esquema = "https" }
    $hostPart = $entrada
    # IPv6 literal sem colchetes
    if ($hostPart -match ':' -and $hostPart -notmatch '^\[') { $hostPart = "[$hostPart]" }
    # Porta ja informada junto do host (ex.: 10.0.0.20:8080)
    if ($hostPart -match '^(\[[^\]]+\]|[^:]+):\d+$') { $WebApiUrl = "${esquema}://$hostPart" }
    else { $WebApiUrl = "${esquema}://${hostPart}:$Porta" }
}

$uri = $null
if (-not [Uri]::TryCreate($WebApiUrl, [UriKind]::Absolute, [ref]$uri) -or ($uri.Scheme -notin @('http', 'https'))) {
    Falha "URL invalida: $WebApiUrl"
    exit 1
}
$WebApiUrl = $uri.GetLeftPart([UriPartial]::Authority)

Write-Host "      Portal       : $WebApiUrl"
Write-Host "      Este servidor: $NomeServidor"
Write-Host "      Instalar em  : $InstallDir"
if ($uri.Scheme -eq 'http') {
    Aviso "comunicacao sem criptografia (http). Adequado apenas para ambiente de teste."
}

# -- 2. Teste de comunicacao ---------------------------------------------------
Passo 2 "Testando a comunicacao com o portal..."

$comunicacaoOk = $true

# 2a. Porta TCP
$tcp = New-Object System.Net.Sockets.TcpClient
try {
    $tentativa = $tcp.BeginConnect($uri.DnsSafeHost, $uri.Port, $null, $null)
    if ($tentativa.AsyncWaitHandle.WaitOne(5000) -and $tcp.Connected) {
        $tcp.EndConnect($tentativa)
        Ok "porta $($uri.Port) de $($uri.DnsSafeHost) acessivel."
    }
    else {
        Falha "sem resposta em $($uri.DnsSafeHost):$($uri.Port) (5s). Verifique DNS, firewall e se o servico RDSMonitor.Web esta rodando."
        $comunicacaoOk = $false
    }
}
catch {
    Falha "nao foi possivel conectar em $($uri.DnsSafeHost):$($uri.Port) - $($_.Exception.Message)"
    $comunicacaoOk = $false
}
finally { $tcp.Close() }

# 2b. API do agente (chave + cadastro)
if ($comunicacaoOk) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $parametros = @{
        Uri         = "$WebApiUrl/api/agent/ping/$NomeServidor"
        Headers     = @{ "X-Agent-Key" = $ApiKey }
        TimeoutSec  = 15
        ErrorAction = "Stop"
    }
    if ($PSVersionTable.PSVersion.Major -lt 6) { $parametros.UseBasicParsing = $true }
    $callbackAnterior = [Net.ServicePointManager]::ServerCertificateValidationCallback
    if ($IgnorarCertificado) {
        if ($PSVersionTable.PSVersion.Major -ge 6) { $parametros.SkipCertificateCheck = $true }
        else { [Net.ServicePointManager]::ServerCertificateValidationCallback = { $true } }
    }

    try {
        $resp = Invoke-RestMethod @parametros
        Ok "portal respondeu e a chave da API foi aceita."
        if (-not $resp.cadastrado) {
            Aviso "o servidor '$NomeServidor' AINDA NAO esta cadastrado no portal."
            Write-Host "      Cadastre-o em Servidores > Novo servidor com exatamente este nome." -ForegroundColor DarkYellow
            Write-Host "      O agente fica tentando e passa a aparecer assim que for cadastrado." -ForegroundColor DarkYellow
        }
        elseif (-not $resp.habilitado) {
            Aviso "o servidor '$NomeServidor' esta cadastrado, mas DESABILITADO no portal."
        }
        else {
            Ok "servidor '$NomeServidor' cadastrado e habilitado no portal."
        }
    }
    catch {
        $status = $null
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
        }
        switch ($status) {
            401     { Falha "a chave da API foi RECUSADA (401). Use a mesma chave do appsettings.Production.json do portal." }
            404     { Falha "endereco respondeu, mas nao e o portal RDSMonitor (ou versao antiga sem /api/agent/ping)." }
            500     { Falha "o portal esta sem AgentSettings:ApiKey configurada (500)." }
            default { Falha "falha na chamada da API: $($_.Exception.Message)" }
        }
        if ($uri.Scheme -eq 'https' -and -not $IgnorarCertificado -and -not $status) {
            Write-Host "      Se o portal usa certificado auto-assinado, rode novamente com -IgnorarCertificado." -ForegroundColor DarkYellow
        }
        $comunicacaoOk = $false
    }
    finally {
        [Net.ServicePointManager]::ServerCertificateValidationCallback = $callbackAnterior
    }
}

if (-not $comunicacaoOk) {
    if (-not $Forcar) {
        Write-Host ""
        Write-Host "Instalacao interrompida. Corrija a comunicacao ou rode com -Forcar para instalar mesmo assim." -ForegroundColor Red
        exit 2
    }
    Aviso "-Forcar informado: seguindo com a instalacao apesar da falha."
}

# -- Ferramentas usadas pelo agente --------------------------------------------
foreach ($ferramenta in @("quser.exe", "logoff.exe", "tsdiscon.exe", "msg.exe")) {
    if (-not (Test-Path (Join-Path $env:SystemRoot "System32\$ferramenta"))) {
        Aviso "$ferramenta nao encontrado em System32 - os comandos que dependem dele vao falhar."
    }
}
$rdsh = Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue
if ($rdsh) {
    $papel = Get-WindowsFeature -Name RDS-RD-Server -ErrorAction SilentlyContinue
    if ($papel -and -not $papel.Installed) {
        Aviso "a funcao 'Host da Sessao da Area de Trabalho Remota' (RDS-RD-Server) nao esta instalada."
        Write-Host "      O agente funciona, mas so vera as sessoes administrativas (max. 2)." -ForegroundColor DarkYellow
    }
}

# -- 3. Instalacao anterior ----------------------------------------------------
Passo 3 "Verificando instalacao anterior..."

$existente = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existente) {
    if ($existente.Status -ne 'Stopped') {
        Write-Host "      Parando o servico atual..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        try { $existente.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch { Aviso "o servico demorou para parar." }
    }
    & sc.exe delete $ServiceName | Out-Null
    # Aguarda o Gerenciador de Servicos liberar o registro
    for ($i = 0; $i -lt 20 -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    Ok "servico anterior removido (sera recriado)."
}
else {
    Ok "nenhuma instalacao anterior."
}

# Garante que nenhum processo do agente ficou preso segurando os arquivos
Get-Process -Name "RDSMonitor.Agent" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue

# -- 4. Arquivos -----------------------------------------------------------------
Passo 4 "Copiando arquivos para $InstallDir..."

if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
Copy-Item -Path (Join-Path $OrigemApp "*") -Destination $InstallDir -Recurse -Force
Get-ChildItem -Path $InstallDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
# Nunca levar a configuracao de desenvolvimento (modo simulacao) para o servidor
Remove-Item (Join-Path $InstallDir "appsettings.Development.json") -Force -ErrorAction SilentlyContinue
Ok "arquivos copiados."

# -- 5. Configuracao -------------------------------------------------------------
Passo 5 "Gravando appsettings.json..."

$config = [ordered]@{
    AgentSettings = [ordered]@{
        ServerName                 = $NomeServidor
        WebApiUrl                  = $WebApiUrl
        ApiKey                     = $ApiKey
        HeartbeatIntervalSeconds   = $HeartbeatSegundos
        CommandPollIntervalSeconds = $ComandosSegundos
        IgnorarErrosCertificado    = [bool]$IgnorarCertificado
    }
    Logging = [ordered]@{
        LogLevel = [ordered]@{
            Default                      = "Information"
            "Microsoft.Hosting.Lifetime" = "Information"
        }
        EventLog = [ordered]@{
            LogLevel = [ordered]@{ Default = "Information" }
        }
    }
}
$settingsPath = Join-Path $InstallDir "appsettings.json"
$config | ConvertTo-Json -Depth 6 | Set-Content -Path $settingsPath -Encoding UTF8

# A pasta guarda a chave da API: acesso somente para Administradores e SYSTEM
& icacls.exe $InstallDir /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" | Out-Null
Ok "configuracao gravada e pasta restrita a Administradores/SYSTEM."

# Fonte do Log de Eventos (o servico grava em Aplicativo > RDSMonitor.Agent)
if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::CreateEventSource($ServiceName, "Application")
}

# -- 6. Servico ------------------------------------------------------------------
Passo 6 "Registrando o servico do Windows..."

$exePath = Join-Path $InstallDir $ExeName
New-Service -Name $ServiceName -BinaryPathName "`"$exePath`"" -DisplayName $ServiceDisplay `
            -Description $ServiceDesc -StartupType Automatic | Out-Null
# Reinicia sozinho em caso de falha (5s, 10s, 30s; zera o contador apos 1h)
& sc.exe failure $ServiceName reset= 3600 actions= restart/5000/restart/10000/restart/30000 | Out-Null
# Inicio automatico atrasado: espera a rede e o DNS estarem prontos apos o boot
& sc.exe config $ServiceName start= delayed-auto | Out-Null
Ok "servico '$ServiceName' registrado (LocalSystem, inicio automatico atrasado)."

# -- 7. Inicio -------------------------------------------------------------------
Passo 7 "Iniciando o servico..."

$inicio = Get-Date
Start-Service -Name $ServiceName
try { (Get-Service $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20)) } catch { }
Start-Sleep -Seconds 5
$svc = Get-Service -Name $ServiceName

if ($svc.Status -eq 'Running') {
    Ok "servico em execucao."
}
else {
    Falha "o servico nao ficou em execucao (status: $($svc.Status))."
}

$eventos = Get-EventLog -LogName Application -Source $ServiceName -After $inicio -ErrorAction SilentlyContinue |
    Where-Object { $_.EntryType -in @('Error', 'Warning') } | Select-Object -First 5
if ($eventos) {
    Write-Host "      Ultimos avisos/erros do agente:" -ForegroundColor DarkYellow
    foreach ($e in $eventos) {
        $msg = ($e.Message -split "`n")[0]
        Write-Host "        [$($e.EntryType)] $msg" -ForegroundColor DarkYellow
    }
}

Write-Host ""
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "  Instalacao concluida" -ForegroundColor Green
Write-Host ""
Write-Host "  Portal      : $WebApiUrl"
Write-Host "  Servidor    : $NomeServidor  (deve estar cadastrado em Servidores)"
Write-Host "  Instalado em: $InstallDir"
Write-Host ""
Write-Host "  Comandos uteis:"
Write-Host "    Get-Service $ServiceName"
Write-Host "    Restart-Service $ServiceName"
Write-Host "    Get-EventLog Application -Source $ServiceName -Newest 20"
Write-Host "    .\uninstall-agent.ps1                 # remover"
Write-Host "==================================================" -ForegroundColor Cyan
