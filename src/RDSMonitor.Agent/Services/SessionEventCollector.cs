using System.Diagnostics.Eventing.Reader;
using RDSMonitor.Agent.Models;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// Lê o ciclo de vida das sessões RDS nos logs do Windows e envia ao portal,
/// que monta o histórico a partir deles:
/// <list type="bullet">
///   <item>TerminalServices-LocalSessionManager/Operational:
///     21 logon · 22 shell iniciado · 23 logoff · 24 desconexão · 25 reconexão ·
///     39 "sessão X desconectada pela sessão Y" · 40 motivo da desconexão;</item>
///   <item>TerminalServices-RemoteConnectionManager/Operational:
///     1149 autenticação RDP aceita (usuário e IP de origem).</item>
/// </list>
/// Os eventos 39 e 40 não viram registros próprios: completam o 24 da mesma
/// sessão (quem pediu a desconexão e o código do motivo).
///
/// Os eventos só são considerados enviados depois que o heartbeat é aceito
/// (<see cref="Confirmar"/>); se o portal estiver fora, são reenviados no
/// próximo ciclo. O portal descarta duplicados.
/// </summary>
public class SessionEventCollector
{
    public const string LogName       = "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational";
    public const string LogConexoes   = "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational";

    private const string FiltroSessoes = "EventID=21 or EventID=22 or EventID=23 or EventID=24 or EventID=25 or EventID=39 or EventID=40";
    private const string FiltroConexoes = "EventID=1149";

    /// <summary>Na primeira leitura após iniciar, busca o que aconteceu nas últimas horas.</summary>
    private static readonly TimeSpan JanelaInicial = TimeSpan.FromHours(24);

    /// <summary>Limite por heartbeat — o restante vai nos próximos ciclos.</summary>
    private const int MaxPorEnvio = 500;

    /// <summary>39 e 40 são gravados junto com o 24; tolerância para associá-los.</summary>
    private static readonly TimeSpan Tolerancia = TimeSpan.FromSeconds(15);

    private readonly ILogger<SessionEventCollector> _log;
    private readonly Dictionary<string, long?> _ultimoConfirmado = new() { [LogName] = null, [LogConexoes] = null };
    private readonly HashSet<string> _avisouErro = new();

    public SessionEventCollector(ILogger<SessionEventCollector> log) => _log = log;

    /// <summary>Eventos ainda não confirmados pelo portal, em ordem cronológica.</summary>
    public List<SessionEventInfo> Coletar()
    {
        var resultado = new List<SessionEventInfo>();
        resultado.AddRange(ConverterSessoes(Ler(LogName, FiltroSessoes)));
        resultado.AddRange(ConverterConexoes(Ler(LogConexoes, FiltroConexoes)));
        return resultado.OrderBy(e => e.TimeUtc).ThenBy(e => e.EventId == 1149 ? 0 : 1).Take(MaxPorEnvio).ToList();
    }

    /// <summary>Marca como entregues os eventos enviados (heartbeat aceito pelo portal).</summary>
    public void Confirmar(IReadOnlyCollection<SessionEventInfo> enviados)
    {
        foreach (var log in _ultimoConfirmado.Keys.ToList())
        {
            var doLog = enviados.Where(e => e.Log == log && e.RecordId > 0).ToList();
            if (doLog.Count > 0)
                _ultimoConfirmado[log] = Math.Max(_ultimoConfirmado[log] ?? 0, doLog.Max(e => e.RecordId));
            else
                _ultimoConfirmado[log] ??= UltimoRecordId(log);   // nada novo: avança a janela para não reler 24h
        }
    }

    private List<EventRecord> Ler(string log, string ids)
    {
        var brutos = new List<EventRecord>();
        try
        {
            var filtro = _ultimoConfirmado[log] is { } ultimo
                ? $"*[System[({ids}) and (EventRecordID > {ultimo})]]"
                : $"*[System[({ids}) and TimeCreated[timediff(@SystemTime) <= {(long)JanelaInicial.TotalMilliseconds}]]]";

            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, filtro));
            for (var ev = reader.ReadEvent(); ev is not null; ev = reader.ReadEvent())
            {
                brutos.Add(ev);
                if (brutos.Count >= MaxPorEnvio * 3) break;
            }
            _avisouErro.Remove(log);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            if (_avisouErro.Add(log))
                _log.LogWarning("Não foi possível ler o log {Log}: {Msg}. Esses eventos não serão enviados " +
                                "(o agente precisa rodar como LocalSystem ou administrador).", log, ex.Message);
        }
        return brutos;
    }

    private static long? UltimoRecordId(string log)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName) { ReverseDirection = true });
            using var ev = reader.ReadEvent();
            return ev?.RecordId ?? 0;
        }
        catch
        {
            return null;
        }
    }

    private static List<SessionEventInfo> ConverterSessoes(List<EventRecord> brutos)
    {
        try
        {
            // 40: sessão + código do motivo · 39: sessão desconectada + sessão que pediu
            var motivos = brutos.Where(e => e.Id == 40 && e.TimeCreated is not null)
                .Select(e => (Sessao: Inteiro(e, 0), Quando: Utc(e), Valor: Inteiro(e, 1))).ToList();
            var autores = brutos.Where(e => e.Id == 39 && e.TimeCreated is not null)
                .Select(e => (Sessao: Inteiro(e, 0), Quando: Utc(e), Valor: Inteiro(e, 1))).ToList();

            int? Associado(List<(int? Sessao, DateTime Quando, int? Valor)> lista, int sessao, DateTime quando) => lista
                .Where(m => m.Sessao == sessao && (m.Quando - quando).Duration() <= Tolerancia)
                .OrderBy(m => (m.Quando - quando).Duration())
                .Select(m => m.Valor)
                .FirstOrDefault();

            var lista = new List<SessionEventInfo>();
            foreach (var e in brutos.Where(e => e.Id is 21 or 22 or 23 or 24 or 25 && e.TimeCreated is not null && e.RecordId is not null))
            {
                var sessao = Inteiro(e, 1);
                if (sessao is null) continue;

                var quando   = Utc(e);
                var endereco = e.Id == 23 ? null : Texto(e, 2);
                lista.Add(new SessionEventInfo
                {
                    Log        = LogName,
                    RecordId   = e.RecordId!.Value,
                    EventId    = e.Id,
                    Type       = Tipo(e.Id),
                    SessionId  = sessao.Value,
                    Username   = SemDominio(Texto(e, 0)),
                    ClientIp   = string.IsNullOrWhiteSpace(endereco) ? null : endereco,
                    TimeUtc    = quando,
                    ReasonCode = e.Id == 24 ? Associado(motivos, sessao.Value, quando) : null,
                    DisconnectedBySessionId = e.Id == 24 ? Associado(autores, sessao.Value, quando) : null,
                });
            }
            return lista;
        }
        finally
        {
            foreach (var ev in brutos) ev.Dispose();
        }
    }

    private static List<SessionEventInfo> ConverterConexoes(List<EventRecord> brutos)
    {
        try
        {
            return brutos
                .Where(e => e.Id == 1149 && e.TimeCreated is not null && e.RecordId is not null)
                .Select(e => new SessionEventInfo
                {
                    Log       = LogConexoes,
                    RecordId  = e.RecordId!.Value,
                    EventId   = 1149,
                    Type      = "Auth",
                    SessionId = 0,                         // o 1149 é anterior à sessão: não tem ID
                    Username  = SemDominio(Texto(e, 0)),
                    ClientIp  = Texto(e, 2) is { Length: > 0 } ip ? ip : null,
                    TimeUtc   = Utc(e),
                })
                .Where(e => e.Username.Length > 0)
                .ToList();
        }
        finally
        {
            foreach (var ev in brutos) ev.Dispose();
        }
    }

    private static string Tipo(int id) => id switch
    {
        21 => "Logon",
        22 => "Shell",
        23 => "Logoff",
        24 => "Disconnect",
        25 => "Reconnect",
        _  => "Outro",
    };

    private static DateTime Utc(EventRecord e) => e.TimeCreated!.Value.ToUniversalTime();

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
