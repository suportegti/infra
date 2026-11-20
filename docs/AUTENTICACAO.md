# Autenticação, senhas e e-mail (1.8.0)

Este documento descreve o que foi implementado na versão 1.8.0 do RDSManager:

1. **Integração de login com o Microsoft 365** (Microsoft Entra ID);
2. **Envio de e-mail para recuperação de senha** com senha temporária;
3. **Expiração de senha** por usuário.

Tudo é configurado pela tela **Administração > Configurações** (permissão
*Configurações do sistema*) e pelo cadastro em **Usuários do sistema**.

---

## 1. Cadastro do usuário

Cada usuário passou a ter:

| Campo | Uso |
|---|---|
| **E-mail** | Destino da senha temporária e identificador do login Microsoft 365. Único (sem diferenciar maiúsculas/minúsculas). Obrigatório para contas Microsoft 365 |
| **Tipo de login** | **Local** (usuário e senha do portal) ou **Microsoft 365** (conta da empresa) |
| **Senha expira** | Só para contas locais: a senha vence após a validade definida em Configurações |
| **Exigir troca de senha** | Já existia: força a pessoa a definir uma senha própria no próximo login |

Ao escolher *Microsoft 365*, os campos de senha somem do formulário: a conta não
tem senha no portal. A lista de usuários mostra os selos **Microsoft 365**,
**Senha vence em X dia(s)** ou **Senha vencida**.

Recomenda-se manter ao menos um administrador **Local** (por exemplo, o `admin`)
para não perder o acesso se o Microsoft 365 estiver indisponível ou mal
configurado. Em último caso, o comando `--reset-senha-admin` (ver README) volta o
`admin` para login Local com senha nova.

---

## 2. Login com Microsoft 365

### Como funciona

- A tela de login mostra o botão **Entrar com Microsoft 365** quando a
  integração está configurada e habilitada.
- O portal usa **OpenID Connect** (fluxo *authorization code* com PKCE) contra o
  tenant configurado. A URI de retorno é `https://<portal>/signin-oidc`.
- Na volta, o portal lê o e-mail da conta (`preferred_username`, `email` ou
  `upn`) e procura um usuário **ativo**, do tipo **Microsoft 365**, com esse
  e-mail. Encontrando, a pessoa entra com o **perfil e as permissões** definidos
  no RDSManager. Não encontrando, aparece *"não há usuário do RDSManager com esse
  e-mail"*.
- Uma conta Microsoft 365 que tentar entrar com usuário e senha recebe o aviso
  para usar o botão da Microsoft.
- Senha, MFA, troca e expiração dessas contas seguem as políticas do Microsoft
  365, não as do portal.
- Logins, falhas e recusas ficam em **Auditoria > Tentativas de acesso**.

### Registro do aplicativo no Entra ID

1. Em **entra.microsoft.com**: *Identidade > Aplicativos > Registros de
   aplicativo > Novo registro*.
2. Tipos de conta: **somente este diretório organizacional**. URI de
   redirecionamento (Web): `https://<endereço do portal>/signin-oidc` (a tela de
   Configurações mostra o endereço exato).
3. Copie o **ID do diretório (locatário)** e o **ID do aplicativo (cliente)**.
4. Em *Certificados e segredos > Novo segredo do cliente*, copie o **Valor**
   (aparece uma única vez) e anote a data de expiração.
5. Em *Permissões de API > Microsoft Graph*: delegadas `openid`, `profile`,
   `email`. Para enviar e-mail pelo Microsoft 365 (seção 3), adicione a permissão
   de **aplicativo** `Mail.Send`. Conceda o **consentimento do administrador**.

### Configuração no portal

Em **Configurações > Microsoft 365**: preencha Tenant, ID do aplicativo, **Chave**
(o segredo) e a data de vencimento da chave. Use **Testar conexão** (confere
tenant, aplicativo e chave obtendo um token) e marque **Habilitar login com
Microsoft 365**. As alterações valem na hora, sem reiniciar o serviço.

### Troca da chave (rotina de segurança)

- A chave é gravada **criptografada** (ASP.NET Data Protection, com as chaves de
  criptografia em `data\chaves` protegidas pelo Windows da máquina) e **nunca é
  exibida** de volta na tela.
- Faltando **30 dias** para vencer, a tela de Configurações e o Dashboard avisam
  os administradores.
- Para trocar: gere um novo segredo no Entra ID, cole em **Chave**, informe a
  nova data e salve. Teste a conexão e só então apague o segredo antigo no Entra.
- Quem alterou e quando fica registrado nas Configurações.

### Requisito: HTTPS

A Microsoft só devolve o login para endereços **https** (a única exceção é
`http://localhost`, útil para testar na própria máquina). Antes de habilitar o
botão no ambiente real, publique o portal com certificado (IIS/proxy reverso ou
`Kestrel:Endpoints` com certificado) e cadastre essa URI no aplicativo do Entra.

---

## 3. Recuperação de senha por e-mail

### Como funciona

1. Na tela de login, **Esqueci minha senha** pede o usuário **ou** o e-mail.
2. Se existir uma conta **local, ativa e com e-mail**, o portal gera uma **senha
   temporária** (12 caracteres, com maiúscula, minúscula, número e símbolo, sem
   caracteres ambíguos) e envia ao e-mail cadastrado.
3. A senha temporária vale **30 minutos**. A senha atual **continua valendo**
   até a pessoa criar a nova, então um pedido feito por terceiros não bloqueia
   ninguém.
4. Ao entrar com a temporária, a pessoa é levada a **definir uma senha nova**.
   Depois disso, a temporária deixa de valer.

### Proteções

- A resposta na tela é **a mesma** exista ou não a conta (não revela usuários).
- No máximo **um envio a cada 2 minutos** por conta, além do limite de
  tentativas por IP.
- Se o envio do e-mail falhar, a senha temporária é descartada.
- Contas Microsoft 365 não recebem senha temporária: recuperam a senha pela
  Microsoft.
- Pedidos e resultados ficam em **Auditoria > Tentativas de acesso**.

### Configuração do envio (Configurações > E-mail)

| Método | Configuração |
|---|---|
| **SMTP** | Servidor, porta (587 com STARTTLS; a 465/SSL implícito não é suportada), usuário e senha (gravada criptografada), remetente |
| **Microsoft 365** | Usa o aplicativo do Entra ID da seção 2 via Microsoft Graph (`sendMail`). Requer a permissão de aplicativo `Mail.Send` e um remetente que seja uma caixa do tenant |

Use **Enviar e-mail de teste** antes de divulgar a função aos usuários.

---

## 4. Expiração de senha

- Em **Configurações > Política de senha**, defina a **validade em dias**
  (padrão 90, de 1 a 3650).
- No cadastro, marque **Senha expira** nos usuários locais que devem seguir a
  política. Contas Microsoft 365 não usam essa opção.
- A contagem parte da data da última troca de senha. Quando vence, o login com a
  senha correta leva à tela de troca com o aviso *"Sua senha venceu"*; a pessoa
  só acessa o portal depois de criar uma nova.
- A lista e a edição do usuário mostram quando a senha vence.
- O evento *Senha expirada* fica em **Auditoria > Tentativas de acesso**.

---

## 5. Atualização para a 1.8.0

- O banco é atualizado sozinho na primeira subida (colunas novas em usuários e a
  tabela `Configuracoes`).
- Na primeira subida, os usuários precisam **entrar de novo**: as chaves de
  criptografia passaram a ficar em `data\chaves`. Inclua `data\` no backup; sem
  essas chaves, os segredos das Configurações precisam ser informados de novo.
- Usuários existentes continuam **Locais**, sem e-mail e sem expiração até que
  o cadastro seja ajustado.

## 6. Situação dos testes

| Item | Validação |
|---|---|
| Expiração de senha (vencimento, troca obrigatória, selos) | Testado localmente |
| Recuperação por e-mail via SMTP (envio, senha temporária, troca, invalidação) | Testado localmente com servidor SMTP de teste |
| Configurador do Microsoft 365, chave criptografada, aviso de vencimento, erros de tenant | Testado localmente |
| Redirecionamento para a Microsoft (client_id, redirect_uri, PKCE) | Testado localmente |
| Login completo com uma conta real do Microsoft 365 | **Pendente**: depende do registro do aplicativo no tenant da empresa |
| Envio de e-mail pelo Microsoft Graph | **Pendente**: depende do aplicativo com `Mail.Send` no tenant |
