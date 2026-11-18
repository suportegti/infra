namespace RDSMonitor.Web.Security;

/// <summary>
/// Catálogo das funcionalidades do portal que podem ser liberadas por perfil.
/// Cada constante é ao mesmo tempo o código gravado no banco (Perfis.Permissoes)
/// e o nome da policy de autorização — use em
/// <c>[Authorize(Policy = Permissoes.SessoesVer)]</c> e
/// <c>&lt;AuthorizeView Policy="@Permissoes.SessoesVer"&gt;</c>.
/// </summary>
public static class Permissoes
{
    // ── Monitoramento ────────────────────────────────────────
    public const string DashboardVer   = "dashboard.ver";
    public const string SessoesVer     = "sessoes.ver";
    public const string ProcessosVer   = "processos.ver";

    // ── Ações nas sessões ────────────────────────────────────
    public const string SessoesMensagem    = "sessoes.mensagem";
    public const string SessoesDesconectar = "sessoes.desconectar";
    public const string SessoesLogoff      = "sessoes.logoff";
    public const string ProcessosDerrubar  = "processos.derrubar";

    // ── Servidores ───────────────────────────────────────────
    public const string ServidoresVer       = "servidores.ver";
    public const string ServidoresGerenciar = "servidores.gerenciar";
    public const string ServidoresReiniciar = "servidores.reiniciar";
    public const string AgentesAtualizar    = "agentes.atualizar";

    // ── Relatórios e auditoria ───────────────────────────────
    public const string RelatoriosVer = "relatorios.ver";
    public const string AuditoriaVer  = "auditoria.ver";

    // ── Administração do sistema ─────────────────────────────
    public const string ComandosManuais   = "comandos.manuais";
    public const string UsuariosGerenciar = "usuarios.gerenciar";
    public const string PerfisGerenciar   = "perfis.gerenciar";

    public sealed record Definicao(string Codigo, string Nome, string Descricao);
    public sealed record Grupo(string Nome, IReadOnlyList<Definicao> Itens);

    /// <summary>Catálogo agrupado, na ordem em que aparece na tela de perfis.</summary>
    public static readonly IReadOnlyList<Grupo> Grupos = new[]
    {
        new Grupo("Monitoramento", new[]
        {
            new Definicao(DashboardVer, "Ver dashboard", "Visão geral dos servidores e sessões."),
            new Definicao(SessoesVer,   "Ver sessões",   "Lista de sessões dos servidores RDS."),
            new Definicao(ProcessosVer, "Ver processos", "Abrir e pesquisar os processos de cada sessão."),
        }),
        new Grupo("Ações nas sessões", new[]
        {
            new Definicao(SessoesMensagem,    "Enviar mensagem",  "Exibir uma mensagem na sessão do usuário."),
            new Definicao(SessoesDesconectar, "Desconectar sessão", "Desconectar mantendo os programas abertos."),
            new Definicao(SessoesLogoff,      "Fazer logoff",     "Encerrar a sessão e todos os programas do usuário."),
            new Definicao(ProcessosDerrubar,  "Derrubar processo", "Encerrar à força um processo do usuário."),
        }),
        new Grupo("Servidores", new[]
        {
            new Definicao(ServidoresVer,       "Ver servidores",       "Tela de servidores, status e último heartbeat."),
            new Definicao(ServidoresGerenciar, "Gerenciar servidores", "Cadastrar, editar, habilitar/desabilitar e excluir."),
            new Definicao(ServidoresReiniciar, "Reiniciar serviço / servidor", "Reiniciar o serviço de Área de Trabalho Remota ou o servidor."),
            new Definicao(AgentesAtualizar,    "Atualizar agentes",    "Enviar pacotes do agente e atualizar os agentes dos servidores pelo portal."),
        }),
        new Grupo("Relatórios e auditoria", new[]
        {
            new Definicao(RelatoriosVer, "Ver relatórios", "Histórico de sessões (logon/logoff)."),
            new Definicao(AuditoriaVer,  "Ver auditoria",  "Log das ações executadas pelos operadores."),
        }),
        new Grupo("Administração do sistema", new[]
        {
            new Definicao(ComandosManuais,   "Comandos manuais",     "Tela de ações administrativas (comando por ID de sessão/PID)."),
            new Definicao(UsuariosGerenciar, "Gerenciar usuários",   "Criar, editar e desabilitar usuários do portal."),
            new Definicao(PerfisGerenciar,   "Gerenciar perfis",     "Criar perfis e definir as permissões de cada um."),
        }),
    };

    public static readonly IReadOnlyList<string> Todas =
        Grupos.SelectMany(g => g.Itens).Select(i => i.Codigo).ToArray();

    public static bool Existe(string codigo) => Todas.Contains(codigo);

    public static string NomeDe(string codigo) =>
        Grupos.SelectMany(g => g.Itens).FirstOrDefault(i => i.Codigo == codigo)?.Nome ?? codigo;

    // ── Perfis padrão do sistema ─────────────────────────────

    public const string PerfilAdministrador = "Administrador";
    public const string PerfilOperador      = "Operador";
    public const string PerfilAuditor       = "Auditor";

    /// <summary>Permissões iniciais (e de "Restaurar padrão") dos perfis do sistema.</summary>
    public static IReadOnlyList<string> PadraoDoPerfil(string perfil) => perfil switch
    {
        PerfilAdministrador => Todas,
        PerfilOperador => new[]
        {
            DashboardVer, SessoesVer, ProcessosVer, ServidoresVer, RelatoriosVer,
        },
        PerfilAuditor => new[]
        {
            DashboardVer, SessoesVer, RelatoriosVer, AuditoriaVer,
        },
        _ => Array.Empty<string>(),
    };
}
