# HTTPS na rede interna (certificado próprio)

O login com **Microsoft 365** só funciona com o portal em **HTTPS**: a Microsoft não
devolve o login para endereços `http://`, exceto `localhost`. Este guia serve para
portais acessados **somente na rede interna**, por IP (ex.: `192.168.153.16`) ou por
um nome interno, sem nome público nem certificado comprado.

## Como funciona

```
                  ┌──────────────────────────────┐
 Estações ──────► │ https://192.168.153.16:5001  │  navegador (HTTPS)
 (confiam na CA)  │                              │
                  │   Portal RDSMonitor.Web      │
 Servidores RDS ─►│ http://192.168.153.16:5000   │  agentes (/api/agent, HTTP)
                  └──────────────────────────────┘
```

- O script `configurar-https.ps1` cria uma **CA interna** ("RDSManager CA Interna"), que
  é um certificado raiz auto-assinado, e, com ela, o **certificado do portal**. Esse
  certificado vale para os IPs e nomes do servidor e para `localhost`.
- As estações passam a confiar no portal quando o **certificado público da CA**
  (`RDSManager-CA.cer`) é instalado nelas como *Autoridade de Certificação Raiz
  Confiável*. Isso é feito **uma única vez**: renovar o certificado do portal não exige
  reinstalar nada nas estações.
- O navegador que abrir `http://…:5000` é **redirecionado** para `https://…:5001`.
- A API dos agentes **não** é redirecionada: os agentes continuam em HTTP na porta 5000,
  sem precisar confiar no certificado e sem reinstalação.

> **Por que não um certificado auto-assinado simples?** Funcionaria, mas cada renovação
> exigiria reinstalar o certificado em todas as estações. Com a CA interna, as estações
> confiam na CA, e o portal pode trocar de certificado à vontade.

## Passo a passo

### 1. No servidor do portal (PowerShell como Administrador)

O script vem no pacote do portal (`RDSMonitor.Web-<versão>.zip`), ao lado do
`install-web.ps1`:

```powershell
.\configurar-https.ps1 -Enderecos 192.168.153.16
```

Sem `-Enderecos`, o certificado inclui todos os IPv4 da máquina, o nome do computador e o
FQDN. Para incluir também um nome interno:

```powershell
.\configurar-https.ps1 -Enderecos 192.168.153.16, rdsmanager.empresa.local
```

O script:

1. cria ou reaproveita a CA interna e exporta o certificado público para
   `C:\RDSMonitor\Web\certificados\RDSManager-CA.cer`;
2. emite o certificado do portal (5 anos; `-ValidadeAnos` muda isso);
3. grava o endpoint HTTPS em `appsettings.Production.json`;
4. libera a porta no firewall, reinicia o serviço e testa `https://localhost:5001/login`.

| Parâmetro | Padrão | Uso |
|---|---|---|
| `-Enderecos` | IPs, nome e FQDN da máquina | IPs e nomes pelos quais o portal é acessado |
| `-PortaHttps` | `5001` | Porta HTTPS. Use `443` para não precisar de porta no endereço |
| `-ValidadeAnos` | `5` | Validade do certificado do portal (a CA vale 15 anos) |
| `-SemRedirecionar` | — | Mantém o HTTP aberto para o navegador, sem redirecionar |
| `-NovaCA` | — | Gera outra CA (exige reinstalar o `.cer` nas estações) |
| `-OrigensPermitidas` / `-SemFirewall` | `Any` | Mesmo uso do `install-web.ps1` |

As chaves privadas (da CA e do portal) ficam no repositório de certificados do
**computador** e são **não exportáveis**.

### 2. Nas estações: confiar na CA

**Por GPO (recomendado, vale para todas as máquinas do domínio):**
*Configuração do Computador > Políticas > Configurações do Windows > Configurações de
Segurança > Políticas de Chave Pública > Autoridades de Certificação Raiz Confiáveis* >
**Importar** > `RDSManager-CA.cer`.

**Em uma máquina** (PowerShell como Administrador):

```powershell
Import-Certificate -FilePath .\RDSManager-CA.cer -CertStoreLocation Cert:\LocalMachine\Root
```

Edge e Chrome usam o repositório do Windows. O Firefox também usa, desde que a opção
`security.enterprise_roots.enabled` esteja ligada (padrão nas versões atuais no Windows).
Feche e abra o navegador depois de instalar.

### 3. No portal

- **Configurações > E-mail > Endereço do portal:** `https://192.168.153.16:5001`. É o
  endereço usado nos links dos e-mails (boas-vindas e recuperação de senha).
- **Microsoft 365:** cadastre no aplicativo do Entra ID a URI de retorno
  `https://192.168.153.16:5001/signin-oidc`. A tela de Configurações mostra a URI exata
  quando aberta pelo endereço HTTPS.

> **IP ou nome?** A Microsoft exige `https` e só aceita `http` para `localhost`. A
> documentação não proíbe IP privado nem porta diferente de 443. Se o Entra ID recusar a
> URI com IP, crie um nome no DNS interno (ex.: `rdsmanager.empresa.local` apontando para
> `192.168.153.16`), rode o script com esse nome em `-Enderecos` e use
> `https://rdsmanager.empresa.local:5001/signin-oidc`. Usar nome é mais prático de
> qualquer forma: se o IP do servidor mudar, só o DNS muda.

## Renovar o certificado

Rode o mesmo comando de novo antes de vencer. A CA é reaproveitada, então as estações
não precisam de nada. Para ver as validades:

```powershell
Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -like "*RDSManager*" |
    Format-Table Subject, NotAfter, Thumbprint
```

## Atualizações do portal

O `install-web.ps1` **preserva** a configuração HTTPS ao atualizar o portal. Não é
preciso rodar o `configurar-https.ps1` de novo a cada versão.

## Agentes em HTTPS (opcional)

Não é necessário. Para isso, instale o `RDSManager-CA.cer` também nos servidores RDS
(mesmo comando acima) e reinstale o agente com
`-ServidorWeb 192.168.153.16 -Porta 5001 -Https`.

## Problemas comuns

| Sintoma | Causa / ação |
|---|---|
| Navegador mostra *"Sua conexão não é particular"* | A CA não está instalada naquela estação (ou o navegador não foi reaberto) |
| *NET::ERR_CERT_COMMON_NAME_INVALID* | O endereço usado não está no certificado. Rode o script de novo incluindo esse IP/nome em `-Enderecos` |
| Portal não sobe depois do script | `Get-EventLog Application -Source RDSMonitor.Web -Newest 10`. Confira se a porta HTTPS está livre |
| `curl` reclama de *REVOCATION_STATUS_UNKNOWN* | A CA interna não publica lista de revogação. Os navegadores e o PowerShell aceitam; no curl, use `--ssl-no-revoke` |
| Login Microsoft 365: *redirect_uri mismatch* (AADSTS50011) | A URI cadastrada no Entra precisa ser idêntica, inclusive a porta: `https://<endereço>:5001/signin-oidc` |
