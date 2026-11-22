using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;

namespace RDSMonitor.Web.Services;

/// <summary>Filtros da tela de auditoria. Datas em horário LOCAL do servidor do portal.</summary>
public sealed class FiltroAuditoria
{
    public DateTime? DeLocal   { get; set; }
    public DateTime? AteLocal  { get; set; }
    public CommandType? Acao   { get; set; }
    public string? Operador    { get; set; }
    public string? Servidor    { get; set; }
    /// <summary>Usuário da sessão, processo, PID ou texto dos detalhes.</summary>
    public string? Alvo        { get; set; }
    public CommandStatus? Status { get; set; }
    public int Limite          { get; set; } = 500;
}

/// <summary>Filtros da tela Tentativas de acesso. Datas em horário de Brasília.</summary>
public sealed class FiltroAcessos
{
    public DateTime? DeLocal  { get; set; }
    public DateTime? AteLocal { get; set; }
    /// <summary>Login digitado (parte do texto, sem diferenciar maiúsculas).</summary>
    public string? Usuario    { get; set; }
    public string? Ip         { get; set; }
    public AcessoResultado? Resultado { get; set; }
    /// <summary>Só tentativas que não entraram (senha errada, bloqueio, limite etc.).</summary>
    public bool SoFalhas      { get; set; }
    public int Limite         { get; set; } = 1_000;
}

public interface IAuditoriaService
{
    Task<(List<AuditoriaAcao> Itens, bool Truncado)> PesquisarAsync(FiltroAuditoria filtro);
    Task<List<string>> ListarOperadoresAsync();

    /// <summary>Tentativas de login (sucesso e falha) com os filtros, mais recentes primeiro.</summary>
    Task<(List<AcessoLog> Itens, bool Truncado)> PesquisarAcessosAsync(FiltroAcessos filtro);

    /// <summary>Logins que aparecem nas tentativas de acesso (sugestões do filtro).</summary>
    Task<List<string>> ListarUsuariosAcessosAsync();
}

public class AuditoriaService : IAuditoriaService
{
    private readonly AppDbContext _db;

    public AuditoriaService(AppDbContext db) => _db = db;

    public async Task<(List<AuditoriaAcao>, bool)> PesquisarAsync(FiltroAuditoria f)
    {
        IQueryable<AuditoriaAcao> q = _db.Auditoria.AsNoTracking();

        // DataHora é gravada em UTC; o filtro chega em horário local.
        if (Utc(f.DeLocal) is { } deUtc)
            q = q.Where(a => a.DataHora >= deUtc);
        if (Utc(f.AteLocal) is { } ateUtc)
            q = q.Where(a => a.DataHora <= ateUtc);
        if (f.Acao is { } acao)
            q = q.Where(a => a.Acao == acao);
        if (f.Status is { } status)
            q = q.Where(a => a.Status == status);

        if (!string.IsNullOrWhiteSpace(f.Operador))
        {
            var op = f.Operador.Trim().ToLower();
            q = q.Where(a => a.OperadorLogin.ToLower().Contains(op) || (a.OperadorNome != null && a.OperadorNome.ToLower().Contains(op)));
        }
        if (!string.IsNullOrWhiteSpace(f.Servidor))
        {
            var sv = f.Servidor.Trim().ToLower();
            q = q.Where(a => a.ServidorNome.ToLower() == sv);
        }
        if (!string.IsNullOrWhiteSpace(f.Alvo))
        {
            var alvo = f.Alvo.Trim().ToLower();
            var pid  = int.TryParse(alvo, out var p) ? p : (int?)null;
            q = q.Where(a => (a.UsuarioAlvo != null && a.UsuarioAlvo.ToLower().Contains(alvo))
                          || (a.Processo != null && a.Processo.ToLower().Contains(alvo))
                          || (a.Detalhes != null && a.Detalhes.ToLower().Contains(alvo))
                          || (pid != null && (a.Pid == pid || a.SessaoId == pid)));
        }

        var limite = Math.Clamp(f.Limite, 1, 10_000);
        var itens  = await q.OrderByDescending(a => a.DataHora).Take(limite + 1).ToListAsync();
        var truncado = itens.Count > limite;
        if (truncado) itens.RemoveAt(itens.Count - 1);
        return (itens, truncado);
    }

    /// <summary>Horário de Brasília (filtros da tela) → UTC, no mesmo formato gravado no banco.</summary>
    internal static DateTime? Utc(DateTime? local) => Horario.ParaUtc(local);

    public Task<List<string>> ListarOperadoresAsync() =>
        _db.Auditoria.AsNoTracking().Select(a => a.OperadorLogin).Distinct().OrderBy(x => x).ToListAsync();

    public async Task<(List<AcessoLog>, bool)> PesquisarAcessosAsync(FiltroAcessos f)
    {
        IQueryable<AcessoLog> q = _db.AcessoLogs.AsNoTracking();

        if (Utc(f.DeLocal) is { } de)   q = q.Where(a => a.DataHora >= de);
        if (Utc(f.AteLocal) is { } ate) q = q.Where(a => a.DataHora <= ate);
        if (!string.IsNullOrWhiteSpace(f.Usuario))
        {
            var u = f.Usuario.Trim().ToLower();
            q = q.Where(a => a.Usuario.ToLower().Contains(u));
        }
        if (!string.IsNullOrWhiteSpace(f.Ip))
        {
            var ip = f.Ip.Trim();
            q = q.Where(a => a.Ip != null && a.Ip.Contains(ip));
        }
        if (f.Resultado is { } r) q = q.Where(a => a.Resultado == r);
        if (f.SoFalhas) q = q.Where(a => FalhasAcesso.Contains(a.Resultado));

        var limite = Math.Clamp(f.Limite, 1, 20_000);
        var itens = await q.OrderByDescending(a => a.DataHora).Take(limite + 1).ToListAsync();
        var truncado = itens.Count > limite;
        if (truncado) itens.RemoveAt(itens.Count - 1);
        return (itens, truncado);
    }

    /// <summary>Resultados que significam "não entrou" (ataque, erro de digitação, conta travada).</summary>
    public static readonly AcessoResultado[] FalhasAcesso =
    {
        AcessoResultado.SenhaIncorreta, AcessoResultado.UsuarioDesconhecido, AcessoResultado.UsuarioDesabilitado,
        AcessoResultado.Bloqueado, AcessoResultado.LimiteDeTaxa, AcessoResultado.ContaMicrosoft365,
    };

    public Task<List<string>> ListarUsuariosAcessosAsync() =>
        _db.AcessoLogs.AsNoTracking().Select(a => a.Usuario).Distinct().OrderBy(x => x).Take(500).ToListAsync();
}

/// <summary>Colunas ordenáveis das tabelas da Auditoria (as mesmas das exportações).</summary>
public static class AuditoriaOrdenacao
{
    public static readonly IReadOnlyDictionary<string, Func<AuditoriaAcao, object?>> Acoes =
        new Dictionary<string, Func<AuditoriaAcao, object?>>
        {
            ["data"]      = a => a.DataHora,
            ["operador"]  = a => a.OperadorNome ?? a.OperadorLogin,
            ["acao"]      = a => RDSMonitor.Web.Components.Pages.Auditoria.NomeAcao(a.Acao),
            ["servidor"]  = a => a.ServidorNome,
            ["alvo"]      = a => a.UsuarioAlvo ?? a.Processo,
            ["detalhes"]  = a => a.Detalhes,
            ["resultado"] = a => a.Status.ToString(),
        };

    public static readonly IReadOnlyDictionary<string, Func<AcessoLog, object?>> Acessos =
        new Dictionary<string, Func<AcessoLog, object?>>
        {
            ["data"]      = a => a.DataHora,
            ["usuario"]   = a => a.Usuario,
            ["ip"]        = a => a.Ip,
            ["resultado"] = a => AcessoLog.Descrever(a.Resultado),
            ["detalhe"]   = a => a.Detalhe,
        };
}
