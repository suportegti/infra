using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;

namespace RDSMonitor.Web.Services;

/// <summary>Filtros do histórico de sessões. Datas no horário de Brasília.</summary>
public sealed class FiltroRelatorio
{
    public DateTime? DeLocal  { get; set; }
    public DateTime? AteLocal { get; set; }
    public string? Servidor   { get; set; }
    public string? Cliente    { get; set; }
    public string? Usuario    { get; set; }
    /// <summary>null = todas, "ativas" = sem logoff, "encerradas" = com logoff, "desconexoes" = com desconexão.</summary>
    public string? Situacao   { get; set; }
    public int Limite         { get; set; } = 1000;
}

public sealed record ResumoRelatorio(
    int Sessoes, int Usuarios, TimeSpan TempoTotal, TimeSpan TempoMedio, int PicoSimultaneo,
    int Desconexoes = 0, int QuedasDeConexao = 0, TimeSpan TempoDesconectado = default);

public interface IRelatorioService
{
    /// <summary>Sessões do período, já com as desconexões/reconexões de cada uma.</summary>
    Task<(List<SessionHistory> Itens, bool Truncado)> PesquisarHistoricoAsync(FiltroRelatorio filtro);
    Task<List<string>> ListarServidoresAsync();
    Task<List<string>> ListarClientesAsync();
    /// <summary>Duração da sessão; sessões ativas contam até agora.</summary>
    TimeSpan Duracao(SessionHistory h);
    /// <summary>Quantidade de desconexões (evento 24) da sessão.</summary>
    int Desconexoes(SessionHistory h);
    /// <summary>Tempo somado em que a sessão ficou desconectada (de cada 24 até o 25 seguinte, ou até o fim).</summary>
    TimeSpan TempoDesconectado(SessionHistory h);
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

        if (Horario.ParaUtc(f.AteLocal) is { } ateUtc)
            q = q.Where(h => h.LogonTime <= ateUtc);
        if (Horario.ParaUtc(f.DeLocal) is { } deUtc)
            q = q.Where(h => h.LogoffTime == null || h.LogoffTime >= deUtc);

        if (!string.IsNullOrWhiteSpace(f.Servidor))
        {
            var sv = f.Servidor.Trim().ToLower();
            q = q.Where(h => h.ServerName.ToLower() == sv);
        }
        if (!string.IsNullOrWhiteSpace(f.Cliente))
        {
            // Sessões gravadas antes do campo Cliente existir: vale o cliente atual do servidor
            var cliente   = f.Cliente.Trim();
            var nomesDoCliente = await _db.Servers.AsNoTracking()
                .Where(s => s.Cliente == cliente).Select(s => s.Name).ToListAsync();
            q = q.Where(h => h.Cliente == cliente || (h.Cliente == null && nomesDoCliente.Contains(h.ServerName)));
        }
        if (!string.IsNullOrWhiteSpace(f.Usuario))
        {
            var u = f.Usuario.Trim().ToLower();
            q = q.Where(h => h.Username.ToLower().Contains(u)
                          || (h.ClientName != null && h.ClientName.ToLower().Contains(u))
                          || (h.ClientIp != null && h.ClientIp.Contains(u)));
        }
        q = f.Situacao switch
        {
            "ativas"      => q.Where(h => h.LogoffTime == null),
            "encerradas"  => q.Where(h => h.LogoffTime != null),
            "desconexoes" => q.Where(h => h.Eventos.Any(e => e.Tipo == SessionEventType.Desconexao)),
            _             => q,
        };

        var limite = Math.Clamp(f.Limite, 1, 50_000);
        var itens  = await q.OrderByDescending(h => h.LogonTime)
                            .Take(limite + 1)
                            .Include(h => h.Eventos.OrderBy(e => e.DataHora))
                            .AsSplitQuery()
                            .ToListAsync();
        var truncado = itens.Count > limite;
        if (truncado) itens.RemoveAt(itens.Count - 1);
        return (itens, truncado);
    }

    public Task<List<string>> ListarServidoresAsync() =>
        _db.SessionHistories.AsNoTracking().Select(h => h.ServerName).Distinct().OrderBy(n => n).ToListAsync();

    public async Task<List<string>> ListarClientesAsync()
    {
        var atuais    = await _db.Servers.AsNoTracking().Where(s => s.Cliente != null).Select(s => s.Cliente!).Distinct().ToListAsync();
        var historico = await _db.SessionHistories.AsNoTracking().Where(h => h.Cliente != null).Select(h => h.Cliente!).Distinct().ToListAsync();
        return atuais.Union(historico, StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public TimeSpan Duracao(SessionHistory h)
    {
        var d = (h.LogoffTime ?? DateTime.UtcNow) - h.LogonTime;
        return d < TimeSpan.Zero ? TimeSpan.Zero : d;
    }

    public int Desconexoes(SessionHistory h) => h.Eventos.Count(e => e.Tipo == SessionEventType.Desconexao);

    public TimeSpan TempoDesconectado(SessionHistory h)
    {
        var total = TimeSpan.Zero;
        DateTime? desde = null;
        foreach (var e in h.Eventos.OrderBy(e => e.DataHora))
        {
            if (e.Tipo == SessionEventType.Desconexao)
                desde ??= e.DataHora;
            else if (desde is { } ini)
            {
                total += e.DataHora - ini;
                desde = null;
            }
        }
        // Ainda desconectada: conta até o logoff (ou até agora, se a sessão segue aberta)
        if (desde is { } aberto)
            total += (h.LogoffTime ?? DateTime.UtcNow) - aberto;

        return total < TimeSpan.Zero ? TimeSpan.Zero : total;
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

        var desconexoes = itens.SelectMany(h => h.Eventos).Where(e => e.Tipo == SessionEventType.Desconexao).ToList();

        return new(itens.Count,
                   itens.Select(h => h.Username.ToLowerInvariant()).Distinct().Count(),
                   total,
                   TimeSpan.FromTicks(total.Ticks / itens.Count),
                   pico,
                   desconexoes.Count,
                   desconexoes.Count(e => e.MotivoCodigo == 0),
                   TimeSpan.FromTicks(itens.Sum(h => TempoDesconectado(h).Ticks)));
    }
}
