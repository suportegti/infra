using System.Diagnostics.Eventing.Reader;
using RDSMonitor.Agent.Models;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// Lê as desconexões e reconexões de sessão do log do Windows
/// "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational":
/// <list type="bullet">
///   <item>24 — sessão desconectada (usuário, ID da sessão, endereço de origem);</item>
///   <item>25 — reconexão bem-sucedida;</item>
///   <item>40 — código do motivo da desconexão, associado ao evento 24 da mesma sessão.</item>
/// </list>
/// Diferente do estado da sessão (amostrado a cada heartbeat), o log registra
/// cada queda e reconexão, mesmo as que duram poucos segundos.
///
/// Os eventos só são considerados enviados depois que o heartbeat é aceito
/// (<see cref="Confirmar"/>); se o portal estiver fora, são reenviados no
/// próximo ciclo. O portal descarta duplicados pelo EventRecordID.
/// </summary>
public class SessionEventCollector
{
    public const string LogName = "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational";

    /// <summary>Na primeira leitura após iniciar, busca o que aconteceu nas últimas horas.</summary>
    private static readonly TimeSpan JanelaInicial = TimeSpan.FromHours(24);

    /// <summary>Limite por heartbeat — o restante vai nos próximos ciclos.</summary>
    private const int MaxPorEnvio = 500;

    /// <summary>O evento 40 é gravado junto com o 24; tolerância para associá-los.</summary>
    private static readonly TimeSpan ToleranciaMotivo = TimeSpan.FromSeconds(15);

    private readonly ILogger<SessionEventCollector> _log;
    private long? _ultimoConfirmado;
    private bool  _avisouErro;

    public SessionEventCollector(ILogger<SessionEventCollector> log) => _log = log;

    /// <summary>Eventos ainda não confirmados pelo portal, em ordem cronológica.</summary>
    public List<SessionEventInfo> Coletar()
    {
        try
        {
            var filtro = _ultimoConfirmado is { } ultimo
                ? $"*[System[(EventID=24 or EventID=25 or EventID=40) and (EventRecordID > {ultimo})]]"
                : $"*[System[(EventID=24 or EventID=25 or EventID=40) and TimeCreated[timediff(@SystemTime) <= {(long)JanelaInicial.TotalMilliseconds}]]]";

            var brutos = new List<EventRecord>();
            using (var reader = new EventLogReader(new EventLogQuery(LogName, PathType.LogName, filtro)))
            {
                for (var ev = reader.ReadEvent(); ev is not null; ev = reader.ReadEvent())
                {
                    brutos.Add(ev);
                    if (brutos.Count >= MaxPorEnvio * 2) break;
                }
            }

            var resultado = Converter(brutos);
            foreach (var ev in brutos) ev.Dispose();

            _avisouErro = false;
            return resultado.Take(MaxPorEnvio).ToList();
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            if (!_avisouErro)
            {
                _log.LogWarning("Não foi possível ler o log {Log}: {Msg}. Desconexões e reconexões não serão enviadas " +
                                "(o agente precisa rodar como LocalSystem ou administrador).", LogName, ex.Message);
                _avisouErro = true;
            }
            return new();
        }
    }

    /// <summary>Marca como entregues os eventos até este RecordId (heartbeat aceito pelo portal).</summary>
    public void Confirmar(IReadOnlyCollection<SessionEventInfo> enviados)
    {
        var reais = enviados.Where(e => e.RecordId > 0).ToList();
        if (reais.Count > 0)
            _ultimoConfirmado = Math.Max(_ultimoConfirmado ?? 0, reais.Max(e => e.RecordId));
        else
            _ultimoConfirmado ??= UltimoRecordIdDoLog();   // nada novo: avança a janela para não reler 24h
    }

    private static long? UltimoRecordIdDoLog()
    {
        try
        {
            var q = new EventLogQuery(LogName, PathType.LogName) { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            using var ev = reader.ReadEvent();
            return ev?.RecordId ?? 0;
        }
        catch
        {
            return null;
        }
    }

    private static List<SessionEventInfo> Converter(List<EventRecord> brutos)
    {
        // Motivos (evento 40): sessão + horário + código
        var motivos = brutos
            .Where(e => e.Id == 40 && e.TimeCreated is not null)
            .Select(e => (Sessao: Inteiro(e, 0), Quando: e.TimeCreated!.Value.ToUniversalTime(), Codigo: Inteiro(e, 1)))
            .Where(m => m.Sessao is not null)
            .ToList();

        var lista = new List<SessionEventInfo>();
        foreach (var e in brutos.Where(e => e.Id is 24 or 25 && e.TimeCreated is not null && e.RecordId is not null))
        {
            var sessao = Inteiro(e, 1);
            if (sessao is null) continue;

            var quando = e.TimeCreated!.Value.ToUniversalTime();
            int? motivo = null;
            if (e.Id == 24)
            {
                motivo = motivos
                    .Where(m => m.Sessao == sessao && (m.Quando - quando).Duration() <= ToleranciaMotivo)
                    .OrderBy(m => (m.Quando - quando).Duration())
                    .Select(m => m.Codigo)
                    .FirstOrDefault();
            }

            var endereco = Texto(e, 2);
            lista.Add(new SessionEventInfo
            {
                RecordId   = e.RecordId!.Value,
                EventId    = e.Id,
                Type       = e.Id == 24 ? "Disconnect" : "Reconnect",
                SessionId  = sessao.Value,
                Username   = SemDominio(Texto(e, 0)),
                ClientIp   = string.IsNullOrWhiteSpace(endereco) ? null : endereco,
                TimeUtc    = quando,
                ReasonCode = motivo,
            });
        }

        return lista.OrderBy(e => e.RecordId).ToList();
    }

    private static string? Texto(EventRecord e, int i)
        => e.Properties.Count > i ? e.Properties[i].Value?.ToString()?.Trim() : null;

    private static int? Inteiro(EventRecord e, int i)
        => int.TryParse(Texto(e, i), out var v) ? v : null;

    private static string SemDominio(string? usuario)
    {
        if (string.IsNullOrWhiteSpace(usuario)) return string.Empty;
        var i = usuario.LastIndexOf('\\');
        return i >= 0 ? usuario[(i + 1)..] : usuario;
    }
}
