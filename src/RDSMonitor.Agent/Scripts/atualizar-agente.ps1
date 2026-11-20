<#
.SYNOPSIS
    Troca os arquivos do RDSMonitor.Agent por uma versão nova, com volta automática.

.DESCRIPTION
    Iniciado pelo próprio agente (AgentUpdater) depois de baixar e conferir o pacote
    enviado pelo portal. Não é para ser executado à mão.

      1. para o serviço (ou o processo, em desenvolvimento);
      2. faz backup da instalação atual;
      3. espelha a versão nova na pasta (remove arquivos que deixaram de existir),
         PRESERVANDO appsettings*.json (servidor, chave, intervalos);
      4. religa e espera o agente novo confirmar (arquivo ok.txt, gravado no primeiro
         heartbeat aceito pelo portal);
      5. sem confirmação no tempo limite, restaura o backup e religa a versão anterior.

    O resultado vai para resultado.json (enviado ao portal no heartbeat seguinte) e o
    passo a passo para atualizacao.log, ambos na pasta de trabalho.
#>
param(
    [Parameter(Mandatory)] [string] $Origem,      # pasta app\ do pacote novo, já extraída
    [Parameter(Mandatory)] [string] $Destino,     # pasta de instalação do agente
    [Parameter(Mandatory)] [string] $Versao,
    [Parameter(Mandatory)] [string] $Trabalho,    # Destino\_atualizacao
    [string] $Servico = '',                       # nome do serviço do Windows
    [int]    $ProcessoId = 0,                     # desenvolvimento: agente rodando como processo
    [int]    $EsperaSegundos = 180
)

$ErrorActionPreference = 'Stop'
$log       = Join-Path $Trabalho 'atualizacao.log'
$ok        = Join-Path $Trabalho 'ok.txt'
$pendente  = Join-Path $Trabalho 'pendente.json'
$nomeTrab  = Split-Path $Trabalho -Leaf
$backup    = Join-Path $Trabalho ('backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$exe       = Join-Path $Destino 'RDSMonitor.Agent.exe'
$script:pidNovo = 0

function Log([string] $msg) {
    Add-Content -Path $log -Value ("{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg) -Encoding UTF8
}

function Resultado([bool] $sucesso, [string] $mensagem) {
    [ordered]@{
        versao   = $Versao
        sucesso  = $sucesso
        mensagem = $mensagem
        em       = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content -Path (Join-Path $Trabalho 'resultado.json') -Encoding UTF8
}

function Copiar([string] $de, [string] $para, [string] $modo, [string[]] $extra) {
    # $modo: /E = copia tudo · /MIR = deixa o destino idêntico à origem (remove o que sobrou da
    # versão anterior). /XD protege a pasta de trabalho; /XF protege as configurações.
    # Códigos de saída do robocopy < 8 = sucesso.
    $args = @($de, $para, $modo, '/R:3', '/W:2', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/XD', $Trabalho, (Join-Path $Destino 'logs')) + $extra
    & robocopy @args | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy falhou ($LASTEXITCODE) copiando de $de para $para" }
}

function Parar {
    if ($Servico) {
        $s = Get-Service -Name $Servico
        if ($s.Status -ne 'Stopped') {
            Stop-Service -Name $Servico -Force
            $s.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        }
    } else {
        foreach ($id in @($ProcessoId, $script:pidNovo)) {
            if ($id -gt 0) {
                Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
                Wait-Process -Id $id -Timeout 30 -ErrorAction SilentlyContinue
            }
        }
    }
    Start-Sleep -Seconds 2   # libera os arquivos
}

function Iniciar {
    if ($Servico) {
        Start-Service -Name $Servico
    } else {
        $p = Start-Process -FilePath $exe -WorkingDirectory $Destino -WindowStyle Hidden -PassThru
        $script:pidNovo = $p.Id
    }
}

try {
    Start-Sleep -Seconds 3   # deixa o agente atual concluir o heartbeat em andamento
    Remove-Item $ok -ErrorAction SilentlyContinue
    Log "Atualizando o agente para $Versao (origem: $Origem)"

    Parar
    Log 'Agente parado. Fazendo backup da instalação atual.'
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    Copiar $Destino $backup '/E' @('/XF', 'atualizacao.log')

    Log 'Copiando a versão nova (appsettings*.json preservados; arquivos que não existem mais na versão nova são removidos).'
    Copiar $Origem $Destino '/MIR' @('/XF', 'appsettings.json', 'appsettings.Development.json')

    Iniciar
    Log "Agente religado. Aguardando confirmação por até $EsperaSegundos s."
    $limite = (Get-Date).AddSeconds($EsperaSegundos)
    while ((Get-Date) -lt $limite -and -not (Test-Path $ok)) { Start-Sleep -Seconds 3 }

    if (-not (Test-Path $ok)) {
        throw "O agente $Versao não confirmou o funcionamento em $EsperaSegundos s"
    }

    Log "Versão $Versao confirmada."
    Resultado $true "Agente atualizado para $Versao."

    # Limpeza: pacote, pasta extraída e backups antigos (mantém o último)
    Remove-Item $pendente, $ok -ErrorAction SilentlyContinue
    Get-ChildItem $Trabalho -Filter '*.zip' | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem $Trabalho -Directory | Where-Object { $_.Name -notlike 'backup-*' } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem $Trabalho -Directory -Filter 'backup-*' | Sort-Object Name -Descending |
        Select-Object -Skip 1 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    exit 0
}
catch {
    $erro = $_.Exception.Message
    Log "FALHA: $erro"
    try {
        if (Test-Path $backup) {
            Log 'Restaurando a versão anterior.'
            Parar
            Copiar $backup $Destino '/MIR' @()
            Iniciar
            Log 'Versão anterior restaurada e religada.'
            $erro = "$erro. A versão anterior foi restaurada."
        }
    } catch {
        Log "FALHA ao restaurar: $($_.Exception.Message)"
        $erro = "$erro. ATENÇÃO: falhou também a restauração ($($_.Exception.Message)); verifique o servidor."
    }
    Remove-Item $pendente -ErrorAction SilentlyContinue
    Resultado $false $erro
    exit 1
}
