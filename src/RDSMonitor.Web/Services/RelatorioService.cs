using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;

namespace RDSMonitor.Web.Services;

/// <summary>Filtros do histórico de sessões. Datas em horário LOCAL do servidor do portal.</summary>
public sealed class FiltroRelatorio
{
    public DateTime? DeLocal  { get; set; }
    public DateTime? AteLocal { get; set; }
    public string? Servidor   { get; set; }
    public string? Usuario    { get; set; }
    /// <summary>null = todas, "ativas" = sem logoff, "encerradas" = com logoff.</summary>
    public string? Situacao   { get; set; }
    public int Limite         { get; set; } = 1000;
}

public sealed record ResumoRelatorio(int Sessoes, int Usuarios, TimeSpan TempoTotal, TimeSpan TempoMedio, int PicoSimultaneo);

public interface IRelatorioService
{
    Task<(List<SessionHistory> Itens, bool Truncado)> PesquisarHistoricoAsync(FiltroRelatorio filtro);
    Task<List<string>> ListarServidoresAsync();
    /// <summary>Duração da sessão; sessões ativas contam até agora.</summary>
    TimeSpan Duracao(SessionHistory h);
    ResumoRelatorio Resumir(IReadOnlyCollection<SessionHistory> itens);
}

public class RelatorioService : IRelatorioService
{
    private readonly AppDbContext _db;

    public RelatorioService(AppDbContext db) => _db = db;

    /// <summary>
    /// Sessões que estiveram abertas em algum momento do período (login antes do
    /// fim e logoff depois do início, ou ainda ativas). Assim, filtrar "hoje das
    /// 08:00 às 09:00" mostra quem estava conectado nessa faixa, mesmo que tenha
    /// entrado antes.
    /// </summary>
    public async Task<(List<SessionHistory>, bool)> PesquisarHistoricoAsync(FiltroRelatorio f)
    {
        IQueryable<SessionHistory> q = _db.SessionHistories.AsNoTracking();

        if (AuditoriaService.Utc(f.AteLocal) is { } ateUtc)
            q = q.Where(h => h.LogonTime <= ateUtc);
        if (AuditoriaService.Utc(f.DeLocal) is { } deUtc)
            q = q.Where(h => h.LogoffTime == null || h.LogoffTime >= deUtc);

        if (!string.IsNullOrWhiteSpace(f.Servidor))
        {
            var sv = f.Servidor.Trim().ToLower();
            q = q.Where(h => h.ServerName.ToLower() == sv);
        }
        if (!string.IsNullOrWhiteSpace(f.Usuario))
        {
            var u = f.Usuario.Trim().ToLower();
            q = q.Where(h => h.Username.ToLower().Contains(u));
        }
        q = f.Situacao switch
        {
            "ativas"     => q.Where(h => h.LogoffTime == null),
            "encerradas" => q.Where(h => h.LogoffTime != null),
            _            => q,
        };

        var limite = Math.Clamp(f.Limite, 1, 50_000);
        var itens  = await q.OrderByDescending(h => h.LogonTime).Take(limite + 1).ToListAsync();
        var truncado = itens.Count > limite;
        if (truncado) itens.RemoveAt(itens.Count - 1);
        return (itens, truncado);
    }

    public Task<List<string>> ListarServidoresAsync() =>
        _db.SessionHistories.AsNoTracking().Select(h => h.ServerName).Distinct().OrderBy(n => n).ToListAsync();

    public TimeSpan Duracao(SessionHistory h)
    {
        var d = (h.LogoffTime ?? DateTime.UtcNow) - h.LogonTime;
        return d < TimeSpan.Zero ? TimeSpan.Zero : d;
    }

    public ResumoRelatorio Resumir(IReadOnlyCollection<SessionHistory> itens)
    {
        if (itens.Count == 0) return new(0, 0, TimeSpan.Zero, TimeSpan.Zero, 0);

        var total = TimeSpan.FromTicks(itens.Sum(h => Duracao(h).Ticks));

        // Pico de sessões simultâneas: varre os eventos de entrada/saída em ordem.
        var eventos = itens.SelectMany(h => new[]
            {
                (Quando: h.LogonTime, Delta: +1),
                (Quando: h.LogoffTime ?? DateTime.MaxValue, Delta: -1),
            })
            .OrderBy(e => e.Quando).ThenBy(e => e.Delta);
        int atual = 0, pico = 0;
        foreach (var e in eventos) { atual += e.Delta; pico = Math.Max(pico, atual); }

        return new(itens.Count,
                   itens.Select(h => h.Username.ToLowerInvariant()).Distinct().Count(),
                   total,
                   TimeSpan.FromTicks(total.Ticks / itens.Count),
                   pico);
    }
}
