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

public interface IAuditoriaService
{
    Task<(List<AuditoriaAcao> Itens, bool Truncado)> PesquisarAsync(FiltroAuditoria filtro);
    Task<List<string>> ListarOperadoresAsync();

    /// <summary>Tentativas de login (sucesso e falha) no período, mais recentes primeiro.</summary>
    Task<(List<AcessoLog> Itens, bool Truncado)> PesquisarAcessosAsync(DateTime? deLocal, DateTime? ateLocal, int limite = 200);
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

    public async Task<(List<AcessoLog>, bool)> PesquisarAcessosAsync(DateTime? deLocal, DateTime? ateLocal, int limite = 200)
    {
        IQueryable<AcessoLog> q = _db.AcessoLogs.AsNoTracking();

        if (Utc(deLocal) is { } de)   q = q.Where(a => a.DataHora >= de);
        if (Utc(ateLocal) is { } ate) q = q.Where(a => a.DataHora <= ate);

        limite = Math.Clamp(limite, 1, 5_000);
        var itens = await q.OrderByDescending(a => a.DataHora).Take(limite + 1).ToListAsync();
        var truncado = itens.Count > limite;
        if (truncado) itens.RemoveAt(itens.Count - 1);
        return (itens, truncado);
    }
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
