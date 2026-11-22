#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Instala (ou atualiza) o portal RDSMonitor.Web como servico do Windows
    (Windows Server 2016/2019/2022/2025), sem IIS.

.DESCRIPTION
    Execute na pasta extraida do pacote RDSMonitor.Web-*.zip, em um PowerShell
    aberto como Administrador. O script:
      1. Para o servico atual (atualizacao) e confere se a porta esta livre
      2. Copia os arquivos para a pasta de instalacao (o banco em data\ e a
         configuracao appsettings.Production.json sao PRESERVADOS)
      3. Grava appsettings.Production.json (porta, banco e chave da API)
      4. Registra o servico (inicio automatico, reinicio em falha)
      5. Cria a regra de entrada no Firewall do Windows
      6. Inicia o servico e testa a pagina de login

    O pacote e "self-contained": NAO e preciso instalar o .NET nem o IIS.

.PARAMETER Porta
    Porta HTTP em que o portal vai escutar. Padrao: 5000.

.PARAMETER ApiKey
    Chave compartilhada com os agentes. Se omitida: mantem a da instalacao
    anterior ou gera uma nova chave aleatoria (mostrada no final).

.PARAMETER NovaChave
    Gera uma chave nova mesmo havendo uma instalacao anterior. ATENCAO: os
    agentes ja instalados precisarao ser reinstalados com a chave nova.

.PARAMETER SenhaAdmin
    Define a senha do usuario 'admin' (minimo 8 caracteres). Sem este
    parametro, a primeira instalacao usa a senha padrao Rds@1234.

.PARAMETER OrigensPermitidas
    Enderecos remotos liberados no firewall (ex.: "10.0.0.0/24"). Padrao: Any.

.PARAMETER SemFirewall
    Nao cria/atualiza a regra de firewall.

.PARAMETER InstallDir
    Pasta de instalacao. Padrao: C:\RDSMonitor\Web

.EXAMPLE
    .\install-web.ps1

.EXAMPLE
    .\install-web.ps1 -Porta 8080 -SenhaAdmin "Uma#SenhaForte1" -OrigensPermitidas "10.0.0.0/24"
#>

[CmdletBinding()]
param(
    [int]    $Porta = 5000,
    [string] $ApiKey,
    [switch] $NovaChave,
    [string] $SenhaAdmin,
    [string] $OrigensPermitidas = "Any",
    [switch] $SemFirewall,
    [string] $InstallDir = "C:\RDSMonitor\Web"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ServiceName    = "RDSMonitor.Web"
$ServiceDisplay = "RDSMonitor - Portal Web"
$ServiceDesc    = "Portal de monitoramento e administracao das sessoes RDS (RDSMonitor)."
$ExeName        = "RDSMonitor.Web.exe"
$RegraFirewall  = "RDSMonitor Web (entrada TCP)"
$ScriptDir      = Split-Path -Parent $MyInvocation.MyCommand.Path
$OrigemApp      = Join-Path $ScriptDir "app"
$DataDir        = Join-Path $InstallDir "data"
$ProdSettings   = Join-Path $InstallDir "appsettings.Production.json"

function Passo($n, $texto) { Write-Host ""; Write-Host "[$n/6] $texto" -ForegroundColor Yellow }
function Ok($texto)        { Write-Host "      OK - $texto" -ForegroundColor Green }
function Aviso($texto)     { Write-Host "      AVISO - $texto" -ForegroundColor DarkYellow }
function Falha($texto)     { Write-Host "      ERRO - $texto" -ForegroundColor Red }

function Nova-ChaveAleatoria {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    return ([Convert]::ToBase64String($bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

Write-Host ""
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "   RDSMonitor.Web - Instalador do portal"            -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

if (-not (Test-Path (Join-Path $OrigemApp $ExeName))) {
    Falha "Pasta 'app' com $ExeName nao encontrada ao lado deste script ($OrigemApp)."
    Write-Host "      Extraia o pacote RDSMonitor.Web-*.zip inteiro e rode o script de dentro dele."
    exit 1
}
if ($Porta -lt 1 -or $Porta -gt 65535) { Falha "Porta invalida: $Porta"; exit 1 }
if ($SenhaAdmin -and $SenhaAdmin.Length -lt 8) { Falha "-SenhaAdmin precisa ter pelo menos 8 caracteres."; exit 1 }

# -- 1. Instalacao anterior e porta -------------------------------------------
Passo 1 "Verificando instalacao anterior e a porta $Porta..."

$existente = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existente) {
    if ($existente.Status -ne 'Stopped') {
        Write-Host "      Parando o servico atual..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        try { $existente.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch { Aviso "o servico demorou para parar." }
    }
    & sc.exe delete $ServiceName | Out-Null
    for ($i = 0; $i -lt 20 -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    Ok "servico anterior removido (sera recriado; banco e configuracao preservados)."
}
else {
    Ok "nenhuma instalacao anterior do servico."
}

Get-Process -Name "RDSMonitor.Web" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue

$ocupada = Get-NetTCPConnection -LocalPort $Porta -State Listen -ErrorAction SilentlyContinue
if ($ocupada) {
    $donos = ($ocupada | ForEach-Object { (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName } | Select-Object -Unique) -join ", "
    Falha "a porta $Porta ja esta em uso por: $donos. Escolha outra com -Porta."
    exit 1
}
Ok "porta $Porta livre."

# -- 2. Arquivos -----------------------------------------------------------------
Passo 2 "Copiando arquivos para $InstallDir..."

foreach ($pasta in @($InstallDir, $DataDir)) {
    if (-not (Test-Path $pasta)) { New-Item -ItemType Directory -Path $pasta -Force | Out-Null }
}
# Copia o aplicativo; data\ e appsettings.Production.json nao existem no pacote,
# portanto nao sao sobrescritos.
Copy-Item -Path (Join-Path $OrigemApp "*") -Destination $InstallDir -Recurse -Force
Get-ChildItem -Path $InstallDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
Remove-Item (Join-Path $InstallDir "appsettings.Development.json") -Force -ErrorAction SilentlyContinue
Ok "arquivos copiados."

# -- 3. Configuracao -------------------------------------------------------------
Passo 3 "Gravando appsettings.Production.json..."

$chaveAnterior = $null
$httpsAnterior = $null   # endpoint HTTPS do configurar-https.ps1 (preservado na atualizacao)
$opcoesHttps   = $null
if (Test-Path $ProdSettings) {
    try {
        $atual = Get-Content $ProdSettings -Raw | ConvertFrom-Json
        if ($atual.PSObject.Properties['AgentSettings'] -and $atual.AgentSettings.PSObject.Properties['ApiKey']) {
            $chaveAnterior = $atual.AgentSettings.ApiKey
        }
        if ($atual.PSObject.Properties['Kestrel'] -and $atual.Kestrel.PSObject.Properties['Endpoints'] -and
            $atual.Kestrel.Endpoints.PSObject.Properties['Https']) {
            $httpsAnterior = $atual.Kestrel.Endpoints.Https
        }
        if ($atual.PSObject.Properties['Https']) { $opcoesHttps = $atual.Https }
    }
    catch { Aviso "appsettings.Production.json atual nao pode ser lido; sera recriado." }
}

$chaveGerada = $false
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    if ($chaveAnterior -and -not $NovaChave) { $ApiKey = $chaveAnterior }
    else { $ApiKey = Nova-ChaveAleatoria; $chaveGerada = $true }
}
$ApiKey = $ApiKey.Trim()
if ($chaveAnterior -and $ApiKey -ne $chaveAnterior) {
    Aviso "a chave da API mudou. Reinstale os agentes com a chave nova."
}

$dbPath = Join-Path $DataDir "rdsmonitor.db"
$config = [ordered]@{
    ConnectionStrings = [ordered]@{ DefaultConnection = "Data Source=$dbPath" }
    AgentSettings     = [ordered]@{ ApiKey = $ApiKey }
    Kestrel           = [ordered]@{
        Endpoints = [ordered]@{
            Http = [ordered]@{ Url = "http://*:$Porta" }
        }
    }
    Logging = [ordered]@{
        LogLevel = [ordered]@{
            Default                = "Information"
            "Microsoft.AspNetCore" = "Warning"
        }
        EventLog = [ordered]@{
            LogLevel = [ordered]@{ Default = "Warning" }
        }
    }
}
if ($httpsAnterior) {
    $config.Kestrel.Endpoints["Https"] = $httpsAnterior
    if ($opcoesHttps) { $config["Https"] = $opcoesHttps }
    Ok "configuracao HTTPS existente preservada ($($httpsAnterior.Url))."
}
$config | ConvertTo-Json -Depth 8 | Set-Content -Path $ProdSettings -Encoding UTF8

# A pasta guarda o banco e a chave da API: acesso somente Administradores e SYSTEM
& icacls.exe $InstallDir /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" | Out-Null
Ok "configuracao gravada (banco em $dbPath); pasta restrita a Administradores/SYSTEM."

if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::CreateEventSource($ServiceName, "Application")
}

# Senha do admin (cria o banco, se preciso, e encerra - nao sobe o site)
if ($SenhaAdmin) {
    Push-Location $InstallDir
    try { $saida = & (Join-Path $InstallDir $ExeName) "--contentRoot=$InstallDir" "--reset-senha-admin=$SenhaAdmin" 2>&1 | Out-String }
    finally { Pop-Location }
    if ($LASTEXITCODE -eq 0 -and $saida -match "redefinida") { Ok "senha do usuario 'admin' definida." }
    else { Aviso "nao foi possivel definir a senha do admin: $($saida.Trim())" }
}

# -- 4. Servico ------------------------------------------------------------------
Passo 4 "Registrando o servico do Windows..."

$exePath = Join-Path $InstallDir $ExeName
New-Service -Name $ServiceName -BinaryPathName "`"$exePath`"" -DisplayName $ServiceDisplay `
            -Description $ServiceDesc -StartupType Automatic | Out-Null
& sc.exe failure $ServiceName reset= 3600 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Ok "servico '$ServiceName' registrado (LocalSystem, inicio automatico)."

# -- 5. Firewall -----------------------------------------------------------------
Passo 5 "Firewall do Windows..."

if ($SemFirewall) {
    Aviso "-SemFirewall: libere a porta TCP $Porta manualmente para os servidores RDS."
}
else {
    Get-NetFirewallRule -DisplayName $RegraFirewall -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    $origens = @($OrigensPermitidas -split '\s*,\s*' | Where-Object { $_ })
    New-NetFirewallRule -DisplayName $RegraFirewall -Direction Inbound -Action Allow -Protocol TCP `
        -LocalPort $Porta -RemoteAddress $origens -Profile Any | Out-Null
    Ok "entrada TCP $Porta liberada para: $($origens -join ', ')."
}

# -- 6. Inicio -------------------------------------------------------------------
Passo 6 "Iniciando o servico e testando o portal..."

Start-Service -Name $ServiceName
$respondeu = $false
for ($i = 0; $i -lt 30 -and -not $respondeu; $i++) {
    Start-Sleep -Seconds 2
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:$Porta/login" -UseBasicParsing -TimeoutSec 5
        if ($r.StatusCode -eq 200) { $respondeu = $true }
    }
    catch { }
}

if ($respondeu) { Ok "portal respondendo em http://localhost:$Porta/login" }
else {
    Falha "o portal nao respondeu em 60s (status do servico: $((Get-Service $ServiceName).Status))."
    Write-Host "      Veja: Get-EventLog Application -Source $ServiceName -Newest 10" -ForegroundColor DarkYellow
}

# -- Resumo ----------------------------------------------------------------------
$enderecos = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    Select-Object -ExpandProperty IPAddress)
$fqdn = [System.Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName

Write-Host ""
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "  Instalacao concluida" -ForegroundColor Green
Write-Host ""
Write-Host "  Acesse o portal em:"
Write-Host "    http://${fqdn}:$Porta"
foreach ($ip in $enderecos) { Write-Host "    http://${ip}:$Porta" }
Write-Host ""
Write-Host "  Chave da API (use no install-agent.ps1 de cada servidor RDS):"
Write-Host "    $ApiKey" -ForegroundColor White
if ($chaveGerada) { Write-Host "    (chave nova gerada nesta instalacao)" -ForegroundColor DarkYellow }
Write-Host ""
if (-not $SenhaAdmin -and -not $chaveAnterior) {
    Write-Host "  Primeiro acesso: usuario admin / senha Rds@1234 - TROQUE a senha." -ForegroundColor DarkYellow
}
Write-Host "  Banco de dados : $dbPath"
Write-Host "  Configuracao   : $ProdSettings"
Write-Host ""
Write-Host "  Proximo passo: cadastre cada servidor RDS em Servidores > Novo servidor"
Write-Host "  e rode o install-agent.ps1 nele, apontando para este endereco."
Write-Host "==================================================" -ForegroundColor Cyan
