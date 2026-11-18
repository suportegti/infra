# RDSManager — v1 (telas de acesso)

Primeira etapa do projeto descrito no escopo: um portal web central para
monitorar e administrar sessões nos servidores RDS, no lugar de scripts ou
consultas WMI avulsas.

Esta entrega cobre **as telas de acesso** e a estrutura de perfis descrita
no escopo (Administrador, Operador, Auditor), como base para as próximas
etapas (sessões reais, servidores, relatórios, agente Windows).

## Stack usada nesta versão

- **ASP.NET Core 8** + **Blazor** (Razor Components, renderização estática
  no servidor por enquanto)
- **SQLite** como banco desta primeira versão (fácil de rodar localmente,
  sem depender de uma instância SQL Server) — a troca para **SQL Server**
  é só mudar o provider do EF Core em `Program.cs` e a connection string
- Autenticação por **cookie**, com um usuário de demonstração por perfil.
  A integração com **Active Directory / Microsoft Entra ID** entra como
  próxima etapa, no ponto já sinalizado em `Services/UserService.cs`

## Estrutura do projeto

```
src/RDSMonitor.Web/
├── Components/
│   ├── App.razor              # documento HTML raiz
│   ├── Routes.razor           # roteador + proteção por autorização
│   ├── Layout/                # menu lateral (por perfil) e shell da página
│   └── Pages/
│       ├── Login.razor         # tela de login
│       ├── AlterarSenha.razor  # troca de senha (login ou já logado)
│       ├── Home.razor          # dashboard (rota "/")
│       ├── Sessoes.razor       # tela de sessões (mock, com ações por perfil)
│       ├── Servidores.razor    # inventário de servidores
│       ├── Relatorios.razor    # placeholder dos relatórios do escopo
│       ├── Administracao.razor # placeholder das ações administrativas
│       ├── Usuarios.razor      # administração de usuários (incluir/editar/desabilitar)
│       ├── UsuariosEditar.razor# formulário de edição de um usuário
│       └── PerfisUsuarios.razor# referência: permissões de cada perfil
├── Data/                      # DbContext (EF Core) e seed inicial
├── Models/                    # AppUser, UserProfile
├── Services/                  # validação de credenciais e administração de usuários (IUserService)
└── wwwroot/css/site.css       # tokens de design (cores, tipografia)
```

## Como rodar

Pré-requisito: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
cd src/RDSMonitor.Web
dotnet restore
dotnet run
```

O banco SQLite (`rdsmonitor.db`) é criado automaticamente na primeira
execução, com os perfis padrão (Administrador, Operador, Auditor) e um único
usuário, o `admin`. Em Development vêm também cinco servidores de exemplo.

## Implantação em servidores (serviços do Windows)

Para instalar o portal e os agentes em Windows Server, veja
[docs/IMPLANTACAO.md](docs/IMPLANTACAO.md). Em resumo:

```powershell
.\deploy\build-pacotes.ps1 -Versao 1.4.0     # gera dist\RDSMonitor.Web-*.zip e dist\RDSMonitor.Agent-*.zip
# no servidor do portal:      .\install-web.ps1 -SenhaAdmin "..."
# em cada servidor RDS:       .\install-agent.ps1 -ServidorWeb <ip-ou-dns> -ApiKey "..."
```

## Usuário inicial

| Usuário | Senha      | Perfil        |
|---------|------------|---------------|
| `admin` | `Rds@1234` | Administrador |

Operador e Auditor são **perfis**, não usuários: crie os usuários nominais em
**Usuários do sistema** e escolha o perfil de cada um. Troque a senha do
`admin` antes de qualquer uso fora do ambiente de desenvolvimento.

## Esqueci a senha do admin — como resetar

Sem apagar o banco nem os outros usuários, rode (dentro de `src/RDSMonitor.Web`):

```bash
dotnet run -- --reset-senha-admin
```

Isso redefine a senha do usuário `admin` para `Rds@1234` e garante que a
conta esteja habilitada, e encerra sem subir o servidor web. Se quiser
escolher a senha nova, use `--reset-senha-admin=SuaSenhaNova` (mínimo 8
caracteres). Depois é só rodar `dotnet run` normalmente e entrar com a nova
senha.

## Novidades desta revisão (visual)

- **Login centralizado**: card único centralizado sobre o fundo, no lugar do
  layout dividido em duas colunas.
- **Tema claro/escuro**: botão de sol/lua no topo (login e dashboard) alterna
  o tema e lembra a escolha no navegador (`localStorage`).
- **Menu lateral com grupo retrátil**: "Administração" agora é uma seção que
  abre/fecha (sem JavaScript de framework — usa `<details>`/`<summary>` do
  HTML), do mesmo jeito que menus como "Vendas"/"IoT" em painéis administrativos
  tradicionais.
- **Menu do usuário em dropdown**: clique no nome do usuário, no canto
  superior direito, para ver o perfil e sair.
- **Menu hambúrguer**: em telas estreitas, o menu lateral vira um painel
  deslizante acionado pelo ícone ☰.
- **Barra de ações (toolbar)**: campo de busca + botões de ação coloridos
  acima das tabelas de Sessões e Servidores (ainda decorativos, aguardando os
  dados reais).
- **Alterar senha**: tela `/alterar-senha`, acessível pelo link na tela de
  login e pelo menu do usuário (já logado). Pede usuário + senha atual +
  nova senha (troca autosserviço — ainda não há recuperação por e-mail, pois
  isso depende da integração com AD/Entra ID prevista para uma próxima
  etapa).

## Administração de usuários (novo)

A tela **Usuários do sistema** (`/usuarios`, só para Administrador) deixou
de ser só uma referência estática e agora administra de verdade os usuários
do painel:

- **Incluir**: formulário retrátil "Novo usuário" (login, nome de exibição,
  perfil e senha inicial).
- **Editar**: tela própria (`/usuarios/editar/{id}`) para alterar nome de
  exibição, perfil e, opcionalmente, redefinir a senha do usuário (útil se
  ele esquecer e não tiver mais acesso).
- **Desabilitar/Habilitar**: um clique alterna o campo `Ativo` do usuário —
  usuário desabilitado não consegue mais fazer login (a validação já
  filtra por `Ativo` em `ValidarCredenciaisAsync`). Usuários **nunca são
  excluídos**, só desabilitados, para preservar o histórico (auditoria e
  logoffs feitos por eles). O banco também barra exclusões com um gatilho
  (`TR_Users_SemExclusao`), mesmo que alguém tente apagar por fora do portal.

A tabela de permissões que antes ficava nessa tela virou uma página própria,
só de referência: **Perfis de usuários** (`/perfis-usuarios`), com a mesma
lista de permissões por perfil (Administrador, Operador, Auditor).

## Perfis e permissões

As permissões são configuráveis por perfil em **Administração > Perfis de
usuários** (catálogo em `src/RDSMonitor.Web/Security/Permissoes.cs`). Padrões:

- **Administrador**: acesso total, sempre (não editável).
- **Operador**: Dashboard, Sessões, processos, Servidores e Relatórios, somente
  visualização.
- **Auditor**: Dashboard, Sessões, Relatórios e Auditoria, somente consulta.

Perfis novos podem ser criados com qualquer combinação. Toda ação dos operadores
(logoff, desconectar, derrubar processo, mensagem, reinícios) fica registrada em
**Auditoria**, com pesquisa por data/hora e exportação para Excel (.xlsx) ou CSV.

## Segurança do login

O login é protegido contra força bruta: a conta é bloqueada por 15 minutos após
5 senhas erradas seguidas, e cada IP tem limite de 10 tentativas por minuto.
Todas as tentativas (sucesso e falha, com usuário e IP) ficam em
**Auditoria > Tentativas de acesso**. Troque a senha padrão do `admin`
(`Rds@1234`) antes de qualquer uso real — ela é pública neste repositório.

## Relatórios

A tela **Relatórios** mostra o histórico de sessões filtrado por período (data e
hora de início e fim, com atalhos: última hora, hoje, ontem, 7 e 30 dias),
servidor, usuário e situação. Entram as sessões que estiveram abertas em algum
momento do período. O resumo traz total de sessões, usuários distintos, pico de
sessões simultâneas e tempo total/médio, e o botão **Exportar Excel** gera um
.xlsx com os mesmos filtros.

## O que ainda é mock nesta versão

Os dados de servidores/sessões exibidos são fixos no código (`@code` das
páginas), só para validar as telas e as permissões por perfil. Os botões de
ação na tela de Sessões ficam desabilitados para Operador/Auditor, mas
mesmo para o Administrador ainda não disparam nenhuma ação real.

## Próximos passos sugeridos (conforme o escopo)

1. Modelar as tabelas `Servers`, `Sessions`, `Processes`, `SessionHistory`,
   `Alerts`, `Commands` e `Audit` no `AppDbContext`.
2. Criar o **Agente Windows** (serviço local em cada RDS) que coleta
   sessões, CPU, memória e processos, e expõe/envia esses dados para a API
   central — essa é a recomendação do escopo em vez de depender só de
   WinRM/WMI remoto.
3. Ligar as telas de Sessões/Servidores aos dados reais recebidos do
   agente, e implementar de fato as ações (logoff, disconnect, matar
   processo, mensagem, shadow session, reiniciar serviço/servidor),
   sempre gravando em `Audit`.
4. Implementar os relatórios (licenças em uso, usuários simultâneos, horas
   trabalhadas, tempo médio de conexão, aplicações mais utilizadas) a
   partir do `SessionHistory`.
5. Substituir a autenticação local por Active Directory / Microsoft Entra
   ID.
6. Avaliar a migração de SQLite para SQL Server quando o volume de dados
   justificar.
