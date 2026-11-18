# RDSMonitor — Guia de implantação em ambiente de teste

Este guia leva o RDSMonitor da máquina de desenvolvimento para servidores Windows
Server reais: o **portal web** em um servidor e o **agente** em cada servidor RDS.

> Os dois componentes rodam como **serviços do Windows**. São publicados como
> *self-contained*, então **não é preciso instalar o .NET nem o IIS** nos servidores.
> Windows Server 2016, 2019, 2022 e 2025 são suportados; não há diferença de
> procedimento entre o 2019 e o 2022.

---

## 1. Como as peças conversam

```
 Navegador do operador ──HTTP:5000──►  PORTAL (RDSMonitor.Web)  ◄──HTTP:5000──  AGENTE (RDSMonitor.Agent)
                                       serviço Windows + SQLite                  serviço em cada servidor RDS
```

- O **agente sempre inicia a conexão** (heartbeat a cada 30 s e busca de comandos a
  cada 3 s). Além disso, **logo após executar um comando** (logoff, desconectar,
  derrubar processo, etc.) o agente envia um heartbeat imediato, para a tela refletir
  o novo estado sem esperar o ciclo de 30 s. O portal nunca conecta no servidor RDS,
  então **só o portal precisa de porta de entrada liberada**. Os intervalos são
  configuráveis em `appsettings.json` (`CommandPollIntervalSeconds`,
  `HeartbeatIntervalSeconds`) ou no instalador (`-ComandosSegundos`, `-HeartbeatSegundos`).
- O agente se identifica pelo **nome do servidor** e pela **chave da API**
  (cabeçalho `X-Agent-Key`). O portal só aceita servidores **cadastrados e
  habilitados** na tela *Servidores*.

### Requisitos

| Item | Servidor do portal | Servidores RDS (agente) |
|---|---|---|
| Sistema | Windows Server 2016+ (testado no fluxo para 2019/2022) | Windows Server 2016+ com a função **Host da Sessão RD** (RDS-RD-Server) |
| Hardware | 2 vCPU, 4 GB RAM, 1 GB livre | consumo baixo (~50–80 MB RAM) |
| Software | nenhum (pacote inclui o runtime) | nenhum (pacote inclui o runtime) |
| Conta de instalação | Administrador local | Administrador local |
| Conta do serviço | LocalSystem | LocalSystem (necessária para logoff, desconectar e encerrar processos de outros usuários) |
| PowerShell | 5.1 (nativo) | 5.1 (nativo) |

### Rede / firewall

| Origem | Destino | Porta | Para quê |
|---|---|---|---|
| Servidores RDS | Portal | TCP 5000 (ou a escolhida) | heartbeat e comandos do agente |
| Estações dos operadores | Portal | TCP 5000 | acesso ao portal pelo navegador |
| — | Servidores RDS | nenhuma | o agente não recebe conexões |

Se usar nome DNS (ex.: `rdsmonitor.lab.local`), crie o registro **A** apontando para
o IP do servidor do portal antes de instalar os agentes.

---

## 2. Gerar os pacotes (máquina de desenvolvimento)

Na raiz do repositório:

```powershell
.\deploy\build-pacotes.ps1 -Versao 1.6.0
```

Resultado em `dist\`:

| Pacote | Conteúdo |
|---|---|
| `RDSMonitor.Web-1.6.0.zip` | `app\`, `install-web.ps1`, `uninstall-web.ps1`, este guia |
| `RDSMonitor.Agent-1.6.0.zip` | `app\`, `install-agent.ps1`, `uninstall-agent.ps1`, este guia |

Copie cada `.zip` para o servidor de destino (compartilhamento, RDP, etc.).

> **Antes de extrair**, desbloqueie o arquivo, senão o Windows marca os scripts como
> baixados da internet: `Unblock-File .\RDSMonitor.Web-1.6.0.zip`

---

## 3. Instalar o portal

No servidor escolhido para o portal, **PowerShell como Administrador**:

```powershell
Unblock-File .\RDSMonitor.Web-1.6.0.zip
Expand-Archive .\RDSMonitor.Web-1.6.0.zip -DestinationPath C:\Temp\RDSMonitor.Web
cd C:\Temp\RDSMonitor.Web
powershell -ExecutionPolicy Bypass -File .\install-web.ps1 -SenhaAdmin "Defina#UmaSenha1"
```

Parâmetros úteis:

| Parâmetro | Padrão | Uso |
|---|---|---|
| `-Porta` | 5000 | porta HTTP do portal |
| `-SenhaAdmin` | — | já define a senha do `admin` (sem ele: `Rds@1234`) |
| `-OrigensPermitidas` | Any | restringe o firewall, ex.: `"10.0.0.0/24,10.0.1.15"` |
| `-ApiKey` | gerada | usa uma chave definida por você |
| `-NovaChave` | — | troca a chave numa reinstalação (os agentes precisam ser reinstalados) |
| `-SemFirewall` | — | não mexe no Firewall do Windows |
| `-InstallDir` | `C:\RDSMonitor\Web` | pasta de instalação |

No final o instalador mostra:

- os endereços de acesso (`http://<nome>:5000` e `http://<IP>:5000`);
- a **chave da API**: **guarde-a**, ela é usada em cada agente.

O que fica no servidor:

| Caminho | Conteúdo |
|---|---|
| `C:\RDSMonitor\Web\` | aplicativo (acesso só Administradores/SYSTEM) |
| `C:\RDSMonitor\Web\data\rdsmonitor.db` | banco SQLite: **faça backup desta pasta** |
| `C:\RDSMonitor\Web\appsettings.Production.json` | porta, banco e chave da API |
| Serviço `RDSMonitor.Web` | início automático, reinício automático em falha |
| Regra de firewall `RDSMonitor Web (entrada TCP)` | entrada na porta escolhida |

Em uma instalação nova de produção o banco nasce **só com o usuário `admin`** e
**sem servidores**. Os servidores de exemplo e os usuários `operador` e `auditor`
existem apenas no ambiente de desenvolvimento.

---

## 4. Cadastrar os servidores RDS no portal

1. Acesse `http://<portal>:5000` e entre como `admin`.
2. Se não usou `-SenhaAdmin`, o portal pede para definir uma senha própria no
   primeiro login.
3. Em **Servidores > Novo servidor**, cadastre cada servidor RDS com o **nome**
   exatamente igual ao que o agente vai usar. O padrão é o nome do computador
   (`hostname`), por exemplo `RDS-TESTE01`. Maiúsculas e minúsculas não importam.
   Preencha o **Cliente** para poder filtrar Sessões e Relatórios por ele.
4. Ajuste os perfis em **Perfis de usuários** (opcional) e crie os usuários dos
   operadores em **Usuários do sistema**.

O agente também pode ser instalado antes do cadastro: o instalador avisa, e o
servidor aparece online assim que for cadastrado.

### Perfis e permissões

Cada usuário tem um **perfil**, e cada perfil tem a lista de funcionalidades que
libera. A tela **Administração > Perfis de usuários** permite:

- **ajustar** as permissões dos perfis padrão **Operador** e **Auditor** (com o
  botão *Restaurar padrão* para voltar ao original);
- **criar** perfis novos (ex.: *Suporte N1* só com "Ver sessões" + "Enviar
  mensagem"), começando vazio ou copiando outro perfil;
- **excluir** perfis criados, quando nenhum usuário os usa.

O perfil **Administrador** tem sempre acesso total e não pode ser editado. O
sistema também não deixa desabilitar, nem trocar o perfil, do **último**
administrador ativo, para que ninguém fique trancado do lado de fora.
Alterações de perfil valem em até 30 segundos, sem o usuário precisar sair.

| Permissão | O que libera |
|---|---|
| Ver dashboard / sessões / processos | telas de monitoramento e a lista/pesquisa de processos |
| Enviar mensagem | botão **Mensagem** na sessão |
| Desconectar sessão / Fazer logoff | botões **Disconnect** / **Logoff** |
| Derrubar processo | botão **Derrubar** na lista de processos |
| Ver / Gerenciar servidores | tela Servidores / cadastrar, editar, desabilitar, excluir |
| Reiniciar serviço / servidor | reiniciar o TermService ou o servidor |
| Ver relatórios / Ver auditoria | histórico de sessões / log de ações dos operadores |
| Comandos manuais, Gerenciar usuários, Gerenciar perfis | menu Administração |

Padrões: **Operador** = ver dashboard, sessões, processos, servidores e relatórios
(sem ações); **Auditor** = ver dashboard, sessões, relatórios e auditoria.

### Auditoria

Toda ação executada pelo portal fica registrada em **Auditoria**: logoff,
desconectar, derrubar processo, mensagem, reiniciar serviço e reiniciar servidor.
Cada registro guarda:

- **quem** fez (login, nome e perfil) e **quando**;
- **onde** foi: servidor, sessão, usuário da sessão, processo e PID;
- o texto da mensagem;
- o **resultado** devolvido pelo agente.

A tela pesquisa por:

- período de data/hora, com atalhos para última hora, hoje, 7 e 30 dias;
- ação, resultado, operador e servidor;
- alvo: usuário, processo, PID ou sessão.

O resultado da pesquisa pode ser exportado para **Excel** (.xlsx) ou **CSV**. A tela
Relatórios tem o mesmo filtro de período (data e hora) e também exporta para Excel.
O registro fica guardado mesmo que o servidor seja excluído depois.

---

## 5. Instalar o agente em cada servidor RDS

**PowerShell como Administrador** no servidor RDS:

```powershell
Unblock-File .\RDSMonitor.Agent-1.6.0.zip
Expand-Archive .\RDSMonitor.Agent-1.6.0.zip -DestinationPath C:\Temp\RDSMonitor.Agent
cd C:\Temp\RDSMonitor.Agent
powershell -ExecutionPolicy Bypass -File .\install-agent.ps1 -ServidorWeb 10.0.0.20 -ApiKey "<chave mostrada pelo portal>"
```

`-ServidorWeb` aceita **IP**, **nome DNS** ou **URL completa**:

```powershell
.\install-agent.ps1 -ServidorWeb 10.0.0.20                          # http://10.0.0.20:5000
.\install-agent.ps1 -ServidorWeb rdsmonitor.lab.local -Porta 8080   # http://rdsmonitor.lab.local:8080
.\install-agent.ps1 -ServidorWeb 10.0.0.20:8080                     # porta junto do endereço
.\install-agent.ps1 -ServidorWeb https://rdsmonitor.lab.local       # HTTPS (ver seção 9)
```

Sem parâmetros, o script **pergunta** o endereço e a chave.

| Parâmetro | Padrão | Uso |
|---|---|---|
| `-ServidorWeb` | perguntado | IP, DNS ou URL do portal |
| `-Porta` | 5000 | quando `-ServidorWeb` não traz porta |
| `-Https` | — | usa `https://` com IP/DNS |
| `-ApiKey` | perguntada | chave do portal |
| `-NomeServidor` | nome do computador | nome cadastrado no portal |
| `-IgnorarCertificado` | — | aceita certificado auto-assinado (só teste) |
| `-Forcar` | — | instala mesmo que o teste de comunicação falhe |
| `-InstallDir` | `C:\RDSMonitor\Agent` | pasta de instalação |

**Antes de instalar**, o script testa a comunicação:

1. Abre uma conexão TCP com o portal (testa DNS, rota e firewall).
2. Chama `/api/agent/ping/<servidor>`, o que confirma a **chave** e informa se o
   servidor está **cadastrado e habilitado**.

Se o teste falhar, nada é instalado e o script explica o motivo.

O serviço `RDSMonitor.Agent` fica com:

- início **automático atrasado**, para esperar a rede subir após o boot;
- reinício automático em falha;
- log em *Visualizador de Eventos > Aplicativo*, fonte `RDSMonitor.Agent`.

> O modo de simulação usado no desenvolvimento **não funciona** nos servidores: o
> agente o desliga fora do ambiente Development, mesmo que esteja no appsettings.

---

## 6. Roteiro de validação

1. **Dashboard**: o servidor aparece **online**, com CPU e memória, em até 30 s.
2. **Sessões**: as sessões do servidor aparecem com usuário, origem (máquina e IP),
   estado e horário de logon no **horário de Brasília**.
3. Crie **um usuário de teste** e conecte-o via RDP. Na sessão dele, abra o Bloco de
   Notas. Depois, no portal:

   | Teste | Resultado esperado |
   |---|---|
   | Pesquisar `notepad` | só a sessão do usuário de teste aparece, com o processo destacado |
   | **Mensagem** para a sessão de teste | janela "Mensagem do administrador" aparece na tela do usuário |
   | **Derrubar** `notepad.exe` | o processo some; a ação fica *Concluído* em Auditoria |
   | `csrss`/`winlogon` (marque *Processos do sistema*) | aparecem como protegidos e não podem ser derrubados |
   | **Desconectar** | a sessão fica *Desconectada*; o RDP do usuário cai, mas os programas continuam |
   | **Logoff** | a sessão some; o histórico em Relatórios é fechado |
   | Usuário que **fechou o RDP sem sair** | aparece como *Desconectada* no portal |
   | **Relatórios** > seta da sessão de teste | linha do tempo com a desconexão (motivo "ferramenta administrativa" ou "conexão perdida") e a reconexão |
   | Filtro **Desconectadas** em Sessões | só as sessões desconectadas aparecem |
   | **Auditoria** | todas as ações acima aparecem com o seu usuário e o resultado |

4. **Nunca** teste Logoff ou Desconectar na **sua própria** sessão de administração
   do servidor.
5. **Reiniciar serviço** (tela Servidores) reinicia o TermService e os serviços
   que dependem dele (ex.: UmRdpService). Isso **desconecta todas as sessões RDP**
   do servidor, inclusive a sua. Os programas continuam abertos e os usuários
   podem reconectar. Faça esse teste fora do horário de uso.

---

## 7. Atualizar uma versão

Gere os pacotes novos e rode **o mesmo instalador** de novo. Ele para o serviço,
troca os arquivos e o religa.

- **Portal**: o banco (`data\`), a chave da API e a porta são **preservados**.
  Não é preciso informar parâmetros, exceto se quiser mudá-los. Tabelas e colunas
  novas (ex.: perfis e auditoria da 1.3) são criadas automaticamente na primeira
  execução, sem apagar dados; os usuários existentes recebem o perfil equivalente.
- **Agente**: informe de novo `-ServidorWeb` e `-ApiKey`, porque a configuração é
  regravada.
- **Portal e agente de versões diferentes** funcionam juntos: um agente 1.4 com o
  portal 1.5 só não envia origem (máquina/IP) nem desconexões; atualize os agentes
  para ter esses dados.

## 8. Desinstalar

```powershell
.\uninstall-agent.ps1 -RemoverArquivos          # servidor RDS
.\uninstall-web.ps1 -RemoverArquivos            # portal (mantém data\ e a configuração)
.\uninstall-web.ps1 -RemoverArquivos -RemoverBanco   # apaga tudo, inclusive o histórico
```

---

## 9. Segurança: antes de ir para produção

O pacote de teste usa **HTTP**. Nesse modo, a chave da API e as senhas do portal
trafegam sem criptografia. Isso é aceitável numa rede de laboratório isolada,
**não** em produção. Para produção:

1. **HTTPS no portal.** Instale um certificado (da CA interna) no repositório
   *Computador Local > Pessoal* do servidor do portal. Depois edite
   `C:\RDSMonitor\Web\appsettings.Production.json`:

   ```json
   "Kestrel": {
     "Endpoints": {
       "Https": {
         "Url": "https://*:5443",
         "Certificate": { "Subject": "rdsmonitor.empresa.local", "Store": "My", "Location": "LocalMachine" }
       }
     }
   }
   ```

   Libere a porta 5443 no firewall, reinicie o serviço (`Restart-Service RDSMonitor.Web`)
   e reinstale os agentes com `-ServidorWeb https://rdsmonitor.empresa.local:5443`.
2. **Firewall**: use `-OrigensPermitidas` com as sub-redes dos servidores RDS e
   dos operadores.
3. **Chave da API forte e exclusiva** do ambiente. O instalador já gera 256 bits
   aleatórios.
4. **Senhas**: troque a senha do `admin` e crie um usuário nominal para cada operador. Usuários não podem ser excluídos, só desabilitados, para não perder o histórico.
5. **Backup** diário de `C:\RDSMonitor\Web\data\`.

### Proteção contra força bruta no login

O portal protege o login contra tentativas de adivinhação de senha:

- **Bloqueio por conta**: após **5 senhas erradas seguidas**, a conta fica
  bloqueada por **15 minutos** — nesse período, nem a senha correta entra. O
  contador zera a cada login bem-sucedido. Vale também para o `admin`.
- **Desbloqueio**: outro administrador remove o bloqueio na tela **Usuários do
  sistema** (botão **Desbloquear**, que aparece só quando a conta está
  bloqueada). Se o `admin` for o único administrador e ficar bloqueado, use no
  servidor `RDSMonitor.Web.exe --desbloquear-admin` (não altera a senha) ou
  espere os 15 minutos.
- **Limite por IP**: no máximo **10 tentativas por minuto** a partir do mesmo
  endereço, independentemente do usuário.
- **Registro**: toda tentativa (sucesso ou falha, com usuário e IP) fica em
  **Auditoria > Tentativas de acesso**, filtrável por período. Use essa tela
  para notar ataques em andamento.

Mesmo com isso, **troque a senha padrão** (`Rds@1234`), que é pública neste
repositório. Os parâmetros (5 tentativas, 15 min, 10/min) estão em
`Services/AutenticacaoService.cs` e na política `login` do `Program.cs`.

---

## 10. Solução de problemas

| Sintoma | Causa provável / ação |
|---|---|
| Instalador do agente: *sem resposta na porta* | Serviço do portal parado, firewall (porta de entrada no portal ou saída na rede) ou DNS. Teste: `Test-NetConnection <portal> -Port 5000` |
| *Chave da API foi RECUSADA (401)* | A chave difere do `AgentSettings:ApiKey` em `appsettings.Production.json` do portal |
| *404 em /api/agent/ping* | O endereço não é o portal, ou o portal é de uma versão anterior |
| Servidor não fica online | Nome não cadastrado ou diferente, ou servidor desabilitado. Veja o log: `Get-EventLog Application -Source RDSMonitor.Agent -Newest 20` |
| Servidor fica *offline* depois de um tempo | O agente parou de enviar heartbeat por 2 min. Veja o status do serviço e o log |
| Portal não abre | `Get-Service RDSMonitor.Web` e `Get-EventLog Application -Source RDSMonitor.Web -Newest 20` |
| Esqueci a senha do admin | No servidor do portal: `cd C:\RDSMonitor\Web; .\RDSMonitor.Web.exe --reset-senha-admin="NovaSenha#1"` (também remove o bloqueio por tentativas) |
| Admin bloqueado por tentativas | Outro administrador clica **Desbloquear** em Usuários do sistema; ou, no servidor: `cd C:\RDSMonitor\Web; .\RDSMonitor.Web.exe --desbloquear-admin` (não altera a senha). O bloqueio também expira sozinho em 15 min |
| Comando fica *Pendente* | Agente parado ou servidor offline. O agente busca comandos a cada 3 s |
| Só aparecem até 2 sessões | O servidor não tem a função Host da Sessão RD (só Administração Remota) |
| Sessão desconectada não aparece | Confirme que o agente é 1.3.0 ou mais novo (as anteriores liam só o `quser` e perdiam essas linhas). Rode o diagnóstico abaixo |

### Diagnóstico de sessões no servidor RDS

Num PowerShell **como administrador**, no servidor RDS (não precisa parar o serviço):

```powershell
& "C:\RDSMonitor\Agent\RDSMonitor.Agent.exe" --diagnostico-sessoes
```

Mostra quatro blocos: o que a API do Windows (WTS) enxerga (com a máquina e o IP
de origem), o que o agente envia ao portal (com a contagem de desconectadas), os
eventos de sessão das últimas 24 h nos logs do Windows (1149 autenticação, 21
logon, 22 shell, 23 logoff, 24 desconexão com motivo e sessão que pediu, 25
reconexão) e a saída do `quser` para comparação. Se o bloco 3 disser "sem
permissão", o agente não está rodando como LocalSystem/administrador e o
histórico fica só com o que o heartbeat percebe. Se a
sessão aparece como `Disconnected` no bloco 2, ela vai aparecer como *Desconectada*
na tela Sessões no próximo heartbeat (até 15 s).

### Logs

```powershell
Get-EventLog Application -Source RDSMonitor.Web   -Newest 20 | Format-List TimeGenerated, EntryType, Message
Get-EventLog Application -Source RDSMonitor.Agent -Newest 20 | Format-List TimeGenerated, EntryType, Message
```
