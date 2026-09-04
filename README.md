# RDS Monitor — v1 (telas de acesso)

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
│       ├── Login.razor        # tela de login
│       ├── Home.razor         # dashboard (rota "/")
│       ├── Sessoes.razor      # tela de sessões (mock, com ações por perfil)
│       ├── Servidores.razor   # inventário de servidores
│       ├── Relatorios.razor   # placeholder dos relatórios do escopo
│       ├── Administracao.razor# placeholder das ações administrativas
│       └── Usuarios.razor     # perfis de acesso
├── Data/                      # DbContext (EF Core) e seed inicial
├── Models/                    # AppUser, UserProfile
├── Services/                  # validação de credenciais (IUserService)
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
execução, já com um usuário de cada perfil.

## Usuários de demonstração

| Usuário    | Senha      | Perfil        |
|------------|------------|---------------|
| `admin`    | `Rds@1234` | Administrador |
| `operador` | `Rds@1234` | Operador      |
| `auditor`  | `Rds@1234` | Auditor       |

Troque ou remova esses usuários antes de qualquer uso fora do ambiente de
desenvolvimento.

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

## O que cada perfil vê

- **Administrador**: todas as telas, incluindo Administração e Usuários do
  sistema; é o único que veria os botões de ação (Logoff, Disconnect,
  Matar processo, Mensagem) habilitados na tela de Sessões.
- **Operador**: Dashboard, Sessões, Servidores e Relatórios — somente
  visualização.
- **Auditor**: Dashboard, Sessões e Relatórios — somente consulta.

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
