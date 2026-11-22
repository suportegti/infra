<#
.SYNOPSIS
    Habilita HTTPS no portal RDSMonitor.Web com um certificado de uma CA interna
    (auto-assinada), valido para o IP e o nome do servidor.

.DESCRIPTION
    Pensado para portais acessados so na rede interna (ex.: http://192.168.153.16:5000),
    sem nome publico nem certificado comprado. Rode no servidor do portal, como Administrador:

      1. Cria (ou reaproveita) a "RDSManager CA Interna": um certificado raiz auto-assinado.
         A chave privada fica neste servidor (nao exportavel). O certificado PUBLICO e
         exportado para <InstallDir>\certificados\RDSManager-CA.cer.
      2. Emite com essa CA o certificado do portal, com os enderecos informados
         (IPs e nomes) e 'localhost'. Certificados anteriores do portal sao removidos.
      3. Grava no appsettings.Production.json o endpoint HTTPS (porta -PortaHttps) e o
         redirecionamento do navegador para HTTPS. A porta HTTP continua ativa para os
         agentes (a API /api/agent nao e redirecionada).
      4. Libera a porta HTTPS no firewall, reinicia o servico e testa o portal.

    Depois, instale RDSManager-CA.cer como "Autoridade de Certificacao Raiz Confiavel" nas
    estacoes que acessam o portal (GPO ou Import-Certificate). Isso e feito UMA vez: ao
    renovar o certificado do portal (rodando este script de novo) as estacoes continuam
    confiando, porque a CA e a mesma.

.PARAMETER Enderecos
    IPs e nomes pelos quais o portal e acessado. Padrao: todos os IPv4 desta maquina, o nome
    do computador e o FQDN. Ex.: -Enderecos 192.168.153.16, rdsmanager.empresa.local

.PARAMETER PortaHttps
    Porta HTTPS. Padrao: 5001 (a 5000 continua em HTTP para os agentes).

.PARAMETER ValidadeAnos
    Validade do certificado do portal. Padrao: 5. A CA vale 15 anos.

.PARAMETER SemRedirecionar
    Nao redireciona o navegador de HTTP para HTTPS (os dois enderecos continuam abertos).

.PARAMETER NovaCA
    Gera uma CA nova mesmo existindo uma valida (exige reinstalar o .cer nas estacoes).

.EXAMPLE
    .\configurar-https.ps1 -Enderecos 192.168.153.16
.EXAMPLE
    .\configurar-https.ps1 -Enderecos 192.168.153.16, rdsmanager.empresa.local -PortaHttps 443
#>
[CmdletBinding()]
param(
    [string[]] $Enderecos,
    [int]      $PortaHttps = 5001,
    [int]      $ValidadeAnos = 5,
    [switch]   $SemRedirecionar,
    [switch]   $NovaCA,
    [string]   $OrigensPermitidas = "Any",
    [switch]   $SemFirewall,
    [string]   $InstallDir = "C:\RDSMonitor\Web"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ServiceName   = "RDSMonitor.Web"
$NomeCA        = "RDSManager CA Interna"
$NomePortal    = "RDSManager Portal"
$RegraFirewall = "RDSMonitor Web HTTPS (entrada TCP)"
$ProdSettings  = Join-Path $InstallDir "appsettings.Production.json"
$PastaCert     = Join-Path $InstallDir "certificados"
$ArquivoCA     = Join-Path $PastaCert "RDSManager-CA.cer"

function Passo($n, $texto) { Write-Host ""; Write-Host "[$n/5] $texto" -ForegroundColor Yellow }
function Ok($texto)        { Write-Host "      OK - $texto" -ForegroundColor Green }
function Aviso($texto)     { Write-Host "      AVISO - $texto" -ForegroundColor DarkYellow }
function Falha($texto)     { Write-Host "      ERRO - $texto" -ForegroundColor Red }

Write-Host ""
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "   RDSMonitor.Web - Configurar HTTPS"                -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Falha "rode este script como Administrador."; exit 1 }
if (-not (Test-Path $ProdSettings)) {
    Falha "$ProdSettings nao encontrado. Instale o portal (install-web.ps1) antes de configurar o HTTPS."
    exit 1
}
if ($PortaHttps -lt 1 -or $PortaHttps -gt 65535) { Falha "Porta invalida: $PortaHttps"; exit 1 }

# -- Enderecos do certificado ---------------------------------------------------
if (-not $Enderecos -or $Enderecos.Count -eq 0) {
    $Enderecos = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
        Select-Object -ExpandProperty IPAddress)
    $Enderecos += $env:COMPUTERNAME
    try { $Enderecos += [System.Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName } catch { }
}
$ips   = New-Object System.Collections.Generic.List[string]
$nomes = New-Object System.Collections.Generic.List[string]
foreach ($e in ($Enderecos + @('localhost', '127.0.0.1'))) {
    $e = "$e".Trim()
    if (-not $e) { continue }
    $ip = $null
    if ([System.Net.IPAddress]::TryParse($e, [ref]$ip)) { if (-not $ips.Contains($e)) { $ips.Add($e) } }
    elseif (-not ($nomes -contains $e.ToLower())) { $nomes.Add($e.ToLower()) }
}

# -- 1. CA interna ----------------------------------------------------------------
Passo 1 "Autoridade certificadora interna ($NomeCA)..."

$ca = $null
if (-not $NovaCA) {
    $ca = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.Subject -like "CN=$NomeCA*" -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddYears(1) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
}
if ($ca) {
    Ok "reaproveitando a CA existente (valida ate $($ca.NotAfter.ToString('dd/MM/yyyy')), $($ca.Thumbprint))."
}
else {
    $ca = New-SelfSignedCertificate -Type Custom `
        -Subject "CN=$NomeCA ($env:COMPUTERNAME), O=RDSManager" `
        -FriendlyName $NomeCA `
        -KeyAlgorithm RSA -KeyLength 4096 -HashAlgorithm SHA256 `
        -KeyUsage CertSign, CRLSign, DigitalSignature `
        -KeyExportPolicy NonExportable `
        -TextExtension @("2.5.29.19={critical}{text}ca=true&pathlength=0") `
        -NotAfter (Get-Date).AddYears(15) `
        -CertStoreLocation Cert:\LocalMachine\My
    Ok "CA criada (valida ate $($ca.NotAfter.ToString('dd/MM/yyyy')), $($ca.Thumbprint))."
}

if (-not (Test-Path $PastaCert)) { New-Item -ItemType Directory -Path $PastaCert -Force | Out-Null }
Export-Certificate -Cert $ca -FilePath $ArquivoCA -Type CERT -Force | Out-Null
# Este servidor tambem precisa confiar na CA (teste local e validacao do certificado pelo Kestrel)
Import-Certificate -FilePath $ArquivoCA -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Ok "certificado publico da CA exportado: $ArquivoCA"

# -- 2. Certificado do portal ----------------------------------------------------
Passo 2 "Emitindo o certificado do portal..."

$san = "2.5.29.17={text}" + ((@($nomes | ForEach-Object { "DNS=$_" }) + @($ips | ForEach-Object { "IPAddress=$_" })) -join '&')
$assunto = "CN=$NomePortal ($env:COMPUTERNAME), O=RDSManager"
$anteriores = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "CN=$NomePortal*" })

$cert = New-SelfSignedCertificate -Type Custom `
    -Subject $assunto -FriendlyName $NomePortal `
    -Signer $ca `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature, KeyEncipherment `
    -KeyExportPolicy NonExportable `
    -TextExtension @($san, "2.5.29.37={text}1.3.6.1.5.5.7.3.1", "2.5.29.19={text}ca=false") `
    -NotAfter (Get-Date).AddYears($ValidadeAnos) `
    -CertStoreLocation Cert:\LocalMachine\My

# Um unico certificado do portal no repositorio: o Kestrel escolhe pelo assunto
foreach ($velho in $anteriores) {
    if ($velho.Thumbprint -ne $cert.Thumbprint) { Remove-Item -Path "Cert:\LocalMachine\My\$($velho.Thumbprint)" -Force }
}
Ok "certificado emitido (valido ate $($cert.NotAfter.ToString('dd/MM/yyyy')), $($cert.Thumbprint))."
Write-Host "      Enderecos: $((@($ips) + @($nomes)) -join ', ')"
if ($anteriores.Count -gt 0) { Ok "$($anteriores.Count) certificado(s) anterior(es) do portal removido(s)." }

# -- 3. Configuracao do portal ---------------------------------------------------
Passo 3 "Gravando a configuracao HTTPS em appsettings.Production.json..."

$cfg = Get-Content $ProdSettings -Raw | ConvertFrom-Json
function Garantir($obj, $nome) {
    if (-not $obj.PSObject.Properties[$nome]) { $obj | Add-Member -NotePropertyName $nome -NotePropertyValue ([pscustomobject]@{}) }
    return $obj.$nome
}
$kestrel   = Garantir $cfg "Kestrel"
$endpoints = Garantir $kestrel "Endpoints"
$https = [pscustomobject][ordered]@{
    Url         = "https://*:$PortaHttps"
    Certificate = [pscustomobject][ordered]@{
        Subject      = "$NomePortal ($env:COMPUTERNAME)"
        Store        = "My"
        Location     = "LocalMachine"
        # A CA interna nao publica lista de revogacao; com $false o Windows tenta checar
        # a revogacao online e pode recusar o certificado. Ele foi emitido acima, por este script.
        AllowInvalid = $true
    }
}
$endpoints | Add-Member -NotePropertyName "Https" -NotePropertyValue $https -Force
$cfg | Add-Member -NotePropertyName "Https" -Force -NotePropertyValue ([pscustomobject][ordered]@{
    Porta                 = $PortaHttps
    RedirecionarNavegador = (-not $SemRedirecionar)
})
$cfg | ConvertTo-Json -Depth 8 | Set-Content -Path $ProdSettings -Encoding UTF8
Ok "HTTPS na porta $PortaHttps$(if ($SemRedirecionar) { ' (sem redirecionar o HTTP)' } else { '; o navegador e redirecionado do HTTP para o HTTPS' })."

# -- 4. Firewall -----------------------------------------------------------------
Passo 4 "Firewall do Windows..."
if ($SemFirewall) {
    Aviso "-SemFirewall: libere a porta TCP $PortaHttps manualmente."
}
else {
    Get-NetFirewallRule -DisplayName $RegraFirewall -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    $origens = @($OrigensPermitidas -split '\s*,\s*' | Where-Object { $_ })
    New-NetFirewallRule -DisplayName $RegraFirewall -Direction Inbound -Action Allow -Protocol TCP `
        -LocalPort $PortaHttps -RemoteAddress $origens -Profile Any | Out-Null
    Ok "entrada TCP $PortaHttps liberada para: $($origens -join ', ')."
}

# -- 5. Reinicio e teste ---------------------------------------------------------
Passo 5 "Reiniciando o portal e testando o HTTPS..."
$servico = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $servico) {
    Aviso "servico $ServiceName nao encontrado; inicie o portal manualmente."
}
else {
    Restart-Service -Name $ServiceName -Force
    $respondeu = $false
    for ($i = 0; $i -lt 30 -and -not $respondeu; $i++) {
        Start-Sleep -Seconds 2
        try {
            $r = Invoke-WebRequest -Uri "https://localhost:$PortaHttps/login" -UseBasicParsing -TimeoutSec 5
            if ($r.StatusCode -eq 200) { $respondeu = $true }
        }
        catch { }
    }
    if ($respondeu) { Ok "portal respondendo em https://localhost:$PortaHttps/login com certificado confiavel." }
    else {
        Falha "o portal nao respondeu em HTTPS em 60s (status do servico: $((Get-Service $ServiceName).Status))."
        Write-Host "      Veja: Get-EventLog Application -Source $ServiceName -Newest 10" -ForegroundColor DarkYellow
    }
}

# -- Resumo ----------------------------------------------------------------------
Write-Host ""
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "  HTTPS configurado" -ForegroundColor Green
Write-Host ""
Write-Host "  Acesse o portal em:"
foreach ($ip in $ips)     { if ($ip -ne '127.0.0.1') { Write-Host "    https://${ip}:$PortaHttps" } }
foreach ($n in $nomes)    { if ($n -ne 'localhost')  { Write-Host "    https://${n}:$PortaHttps" } }
Write-Host ""
Write-Host "  1) Instale a CA nas estacoes que acessam o portal (uma unica vez):" -ForegroundColor White
Write-Host "       Arquivo: $ArquivoCA"
Write-Host "       GPO: Configuracao do Computador > Politicas > Configuracoes do Windows >"
Write-Host "            Configuracoes de Seguranca > Politicas de Chave Publica >"
Write-Host "            Autoridades de Certificacao Raiz Confiaveis > Importar"
Write-Host "       Ou, em cada estacao (PowerShell como Administrador):"
Write-Host "            Import-Certificate -FilePath RDSManager-CA.cer -CertStoreLocation Cert:\LocalMachine\Root"
Write-Host ""
Write-Host "  2) No portal: Configuracoes > E-mail > Endereco do portal = https://<endereco>:$PortaHttps" -ForegroundColor White
Write-Host "  3) Microsoft 365: cadastre no aplicativo do Entra ID a URI de retorno" -ForegroundColor White
foreach ($ip in $ips)  { if ($ip -ne '127.0.0.1') { Write-Host "       https://${ip}:$PortaHttps/signin-oidc" } }
foreach ($n in $nomes) { if ($n -ne 'localhost')  { Write-Host "       https://${n}:$PortaHttps/signin-oidc" } }
Write-Host ""
Write-Host "  Os agentes continuam usando o endereco HTTP (porta do install-web.ps1); nada muda neles."
Write-Host "==================================================" -ForegroundColor Cyan
