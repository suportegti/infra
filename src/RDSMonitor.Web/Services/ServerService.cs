using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models.Dtos;

namespace RDSMonitor.Web.Services;

// ─────────────────────────────────────────────
//  Resultados
// ─────────────────────────────────────────────

public enum HeartbeatResult
{
    Aceito,
    NaoCadastrado,
    Desabilitado,
}

public enum ServerOperationResult
{
    Sucesso,
    NaoEncontrado,
    NomeInvalido,
    NomeJaExiste,
}

// ─────────────────────────────────────────────
//  Interface
// ─────────────────────────────────────────────

public interface IServerService
{
    /// <summary>
    /// Servidores com as sessões ativas, ordenados por nome. Por padrão só os
    /// habilitados — a tela de cadastro pede também os desabilitados.
    /// </summary>
    Task<List<Server>> GetAllWithSessionsAsync(bool incluirDesabilitados = false);

    Task<Server?> GetByIdAsync(int id);

    /// <summary>Processos de um servidor; se <paramref name="rdsSessionId"/> vier, só os daquela sessão.</summary>
    Task<List<ServerProcess>> GetProcessesAsync(int serverId, int? rdsSessionId = null);

    // ── Cadastro ─────────────────────────────────────────────
    Task<ServerOperationResult> CreateAsync(string name, string? displayName, string? cliente = null);
    Task<ServerOperationResult> UpdateAsync(int id, string name, string? displayName, string? cliente = null);

    /// <summary>Clientes distintos cadastrados nos servidores (para filtros).</summary>
    Task<List<string>> GetClientesAsync();
    Task<ServerOperationResult> SetEnabledAsync(int id, bool enabled);

    /// <summary>Exclui o servidor com suas sessões, processos e comandos (o histórico de sessões é mantido).</summary>
    Task<ServerOperationResult> DeleteAsync(int id);

    // ── Agente ───────────────────────────────────────────────
    /// <summary>
    /// Processa o heartbeat do agente: estado do servidor, sessões (com
    /// histórico) e a lista de processos. Recusa servidores não cadastrados
    /// ou desabilitados.
    /// </summary>
    Task<HeartbeatResult> ProcessHeartbeatAsync(AgentHeartbeatDto heartbeat);
}

// ─────────────────────────────────────────────
//  Implementação
// ─────────────────────────────────────────────

public class ServerService : IServerService
{
    /// <summary>Nome de host Windows: letras, números, ponto, hífen e sublinhado.</summary>
    private static readonly Regex NomeValido = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$");

    /// <summary>Limite de segurança de processos aceitos por heartbeat.</summary>
    private const int MaxProcessos = 10_000;

    private readonly AppDbContext _db;
    private readonly ILogger<ServerService> _log;

    public ServerService(AppDbContext db, ILogger<ServerService> log)
    {
        _db  = db;
        _log = log;
    }

    // ── Consultas ────────────────────────────────────────────
    // AsNoTracking: nas páginas interativas o DbContext vive o circuito inteiro;
    // sem isso o EF devolveria as entidades já rastreadas com valores antigos
    // a cada atualização automática.

    public Task<List<Server>> GetAllWithSessionsAsync(bool incluirDesabilitados = false)
        => _db.Servers
              .AsNoTracking()
              .Where(s => incluirDesabilitados || s.IsEnabled)
              .Include(s => s.Sessions)
              .OrderBy(s => s.Name)
              .ToListAsync();

    public Task<Server?> GetByIdAsync(int id)
        => _db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);

    public Task<List<ServerProcess>> GetProcessesAsync(int serverId, int? rdsSessionId = null)
        => _db.Processes
              .AsNoTracking()
              .Where(p => p.ServerId == serverId
                       && (rdsSessionId == null || p.RdsSessionId == rdsSessionId))
              .OrderByDescending(p => p.CpuPercent)
              .ThenByDescending(p => p.MemoryMb)
              .ToListAsync();

    // ── Cadastro ─────────────────────────────────────────────

    public async Task<List<string>> GetClientesAsync()
    {
        var lista = await _db.Servers.AsNoTracking()
            .Where(s => s.Cliente != null && s.Cliente != "")
            .Select(s => s.Cliente!)
            .Distinct()
            .ToListAsync();
        return lista.OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<ServerOperationResult> CreateAsync(string name, string? displayName, string? cliente = null)
    {
        name = name?.Trim() ?? string.Empty;
        if (!NomeValido.IsMatch(name)) return ServerOperationResult.NomeInvalido;

        if (await NomeEmUsoAsync(name, ignorarId: null)) return ServerOperationResult.NomeJaExiste;

        _db.Servers.Add(new Server
        {
            Name        = name,
            DisplayName = Limpar(displayName),
            Cliente     = Limpar(cliente),
            IsEnabled   = true,
        });
        await _db.SaveChangesAsync();
        _log.LogInformation("Servidor {Name} cadastrado.", name);
        return ServerOperationResult.Sucesso;
    }

    public async Task<ServerOperationResult> UpdateAsync(int id, string name, string? displayName, string? cliente = null)
    {
        var server = await _db.Servers.FindAsync(id);
        if (server is null) return ServerOperationResult.NaoEncontrado;

        name = name?.Trim() ?? string.Empty;
        if (!NomeValido.IsMatch(name)) return ServerOperationResult.NomeInvalido;
        if (await NomeEmUsoAsync(name, ignorarId: id)) return ServerOperationResult.NomeJaExiste;

        server.Name        = name;
        server.DisplayName = Limpar(displayName);
        server.Cliente     = Limpar(cliente);
        await _db.SaveChangesAsync();

        // Sessões ainda abertas passam a contar para o novo cliente; as encerradas mantêm o de antes
        await _db.SessionHistories
            .Where(h => h.ServerName == server.Name && h.LogoffTime == null)
            .ExecuteUpdateAsync(u => u.SetProperty(h => h.Cliente, server.Cliente));
        _db.Entry(server).State = EntityState.Detached;   // contexto longo do circuito
        return ServerOperationResult.Sucesso;
    }

    public async Task<ServerOperationResult> SetEnabledAsync(int id, bool enabled)
    {
        var server = await _db.Servers.FindAsync(id);
        if (server is null) return ServerOperationResult.NaoEncontrado;

        server.IsEnabled = enabled;
        if (!enabled)
        {
            // Desabilitado = fora do monitoramento: some das telas e o agente é recusado.
            server.IsOnline = false;
            await _db.Sessions.Where(s => s.ServerId == id).ExecuteDeleteAsync();
            await _db.Processes.Where(p => p.ServerId == id).ExecuteDeleteAsync();
        }
        await _db.SaveChangesAsync();
        _db.Entry(server).State = EntityState.Detached;
        _log.LogInformation("Servidor {Name} {Acao}.", server.Name, enabled ? "habilitado" : "desabilitado");
        return ServerOperationResult.Sucesso;
    }

    public async Task<ServerOperationResult> DeleteAsync(int id)
    {
        var afetados = await _db.Servers.Where(s => s.Id == id).ExecuteDeleteAsync();   // cascata: sessões, processos, comandos
        if (afetados == 0) return ServerOperationResult.NaoEncontrado;
        _log.LogInformation("Servidor {Id} excluído.", id);
        return ServerOperationResult.Sucesso;
    }

    private Task<bool> NomeEmUsoAsync(string name, int? ignorarId)
        => _db.Servers.AnyAsync(s => s.Name.ToLower() == name.ToLower() && s.Id != ignorarId);

    private static string? Limpar(string? texto)
        => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    // ── Heartbeat ────────────────────────────────────────────

    public async Task<HeartbeatResult> ProcessHeartbeatAsync(AgentHeartbeatDto dto)
    {
        var server = await _db.Servers
            .Include(s => s.Sessions)
            .FirstOrDefaultAsync(s => s.Name.ToLower() == dto.ServerName.ToLower());

        if (server is null)
        {
            _log.LogWarning("Heartbeat recusado: servidor {Server} não cadastrado.", dto.ServerName);
            return HeartbeatResult.NaoCadastrado;
        }
        if (!server.IsEnabled)
        {
            _log.LogDebug("Heartbeat ignorado: servidor {Server} desabilitado.", dto.ServerName);
            return HeartbeatResult.Desabilitado;
        }

        var agora = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();

        server.IsOnline       = true;
        server.LastHeartbeat  = agora;
        server.CpuPercent     = Math.Round(dto.CpuPercent, 1);
        server.MemoryUsedGb   = Math.Round(dto.MemoryUsedGb, 2);
        server.MemoryTotalGb  = Math.Round(dto.MemoryTotalGb, 2);

        // O banco guarda tudo em UTC (as telas convertem com ToLocalTime).
        foreach (var sessionDto in dto.Sessions)
            sessionDto.LogonTime = ParaUtc(sessionDto.LogonTime);

        // Registros de histórico ainda abertos deste servidor (um por sessão viva)
        var abertos = await _db.SessionHistories
            .Where(h => h.ServerName == server.Name && h.LogoffTime == null)
            .ToListAsync();

        SessionHistory? AbertoDe(int rdsSessionId, string username) => abertos
            .Where(h => h.RdsSessionId == rdsSessionId && h.Username == username && h.LogoffTime == null)
            .OrderByDescending(h => h.Id)
            .FirstOrDefault();

        // ── Sessões que sumiram → fechar histórico e remover ─
        var reportedIds = dto.Sessions.Select(s => s.SessionId).ToHashSet();
        var removed     = server.Sessions.Where(s => !reportedIds.Contains(s.RdsSessionId)).ToList();

        foreach (var s in removed)
        {
            // Fecha o registro de histórico aberto quando a sessão apareceu.
            // Só cria um novo (já fechado) se não houver registro aberto —
            // ex.: sessão que existia antes de o histórico ser implantado.
            var aberto = AbertoDe(s.RdsSessionId, s.Username);
            if (aberto is null)
            {
                aberto = new SessionHistory
                {
                    ServerName   = server.Name,
                    RdsSessionId = s.RdsSessionId,
                    Username     = s.Username,
                    LogonTime    = s.LogonTime,
                    Cliente      = server.Cliente,
                    ClientName   = s.ClientName,
                    ClientIp     = s.ClientIp,
                };
                _db.SessionHistories.Add(aberto);
            }

            aberto.LogoffTime   = agora;
            aberto.LogoffReason = "Agente";

            _db.Sessions.Remove(s);
        }

        // ── Upsert das sessões reportadas ────────────────────
        foreach (var sessionDto in dto.Sessions)
        {
            var existing = server.Sessions.FirstOrDefault(s => s.RdsSessionId == sessionDto.SessionId);
            var nomeCliente = Opcional(sessionDto.ClientName, 100);
            var ipCliente   = Opcional(sessionDto.ClientIp, 64);

            // O Windows reaproveita IDs de sessão: se outro login ocupou o mesmo ID
            // entre dois heartbeats (outro usuário ou outro horário de logon), a
            // sessão anterior terminou — fecha o histórico dela e abre um novo.
            var anterior = existing is null ? null : AbertoDe(existing.RdsSessionId, existing.Username);
            var reaproveitada = existing is not null
                && (!string.Equals(existing.Username, sessionDto.Username, StringComparison.OrdinalIgnoreCase)
                    || (anterior is not null && Math.Abs((anterior.LogonTime - sessionDto.LogonTime).TotalMinutes) > 2));
            if (reaproveitada)
            {
                if (anterior is not null)
                {
                    // A sessão anterior terminou antes do novo logon (melhor estimativa disponível)
                    anterior.LogoffTime   = sessionDto.LogonTime > anterior.LogonTime && sessionDto.LogonTime < agora
                        ? sessionDto.LogonTime
                        : agora;
                    anterior.LogoffReason = "Agente";
                }
                existing!.ClientName = null;
                existing.ClientIp    = null;
            }

            if (existing is null || reaproveitada)
            {
                if (existing is null)
                {
                    existing = new Session
                    {
                        ServerId     = server.Id,
                        RdsSessionId = sessionDto.SessionId,
                        LogonTime    = sessionDto.LogonTime,
                    };
                    _db.Sessions.Add(existing);
                }

                // Inicia o registro de histórico (LogoffTime preenchido depois)
                var novo = new SessionHistory
                {
                    ServerName   = server.Name,
                    RdsSessionId = sessionDto.SessionId,
                    Username     = sessionDto.Username,
                    LogonTime    = sessionDto.LogonTime,
                    Cliente      = server.Cliente,
                    ClientName   = nomeCliente,
                    ClientIp     = ipCliente,
                };
                _db.SessionHistories.Add(novo);
                abertos.Add(novo);
            }
            else if (nomeCliente is not null || ipCliente is not null)
            {
                // Reconectou (talvez de outra máquina): o histórico guarda a última origem
                var aberto = AbertoDe(sessionDto.SessionId, sessionDto.Username);
                if (aberto is not null)
                {
                    aberto.ClientName = nomeCliente ?? aberto.ClientName;
                    aberto.ClientIp   = ipCliente   ?? aberto.ClientIp;
                }
            }

            existing.Username        = sessionDto.Username;
            existing.SessionName     = sessionDto.SessionName;
            existing.State           = ParseState(sessionDto.State);
            existing.LogonTime       = sessionDto.LogonTime;
            existing.IdleTimeMinutes = sessionDto.IdleTimeMinutes;
            existing.CpuPercent      = Math.Round(sessionDto.CpuPercent, 1);
            existing.MemoryMb        = Math.Round(sessionDto.MemoryMb, 1);
            existing.LastSeen        = agora;

            // Sessão desconectada não informa origem: mantém a última conhecida
            if (nomeCliente is not null) existing.ClientName = nomeCliente;
            if (ipCliente   is not null) existing.ClientIp   = ipCliente;
        }

        // ── Desconexões e reconexões (eventos 24/25 do Windows) ─
        if (dto.Events.Count > 0)
            await GravarEventosAsync(server, dto.Events, abertos);

        // ── Processos: substitui a "foto" anterior do servidor ─
        await _db.Processes.Where(p => p.ServerId == server.Id).ExecuteDeleteAsync();

        _db.Processes.AddRange(dto.Processes
            .Where(p => p.RdsSessionId > 0)
            .Take(MaxProcessos)
            .Select(p => new ServerProcess
            {
                ServerId     = server.Id,
                Pid          = p.Pid,
                Name         = Truncar(p.Name, 260),
                Username     = p.Username is null ? null : Truncar(p.Username, 150),
                RdsSessionId = p.RdsSessionId,
                MemoryMb     = Math.Round(p.MemoryMb, 1),
                CpuPercent   = Math.Round(p.CpuPercent, 1),
                IsProtected  = p.IsProtected,
                LastSeen     = agora,
            }));

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        _log.LogInformation("Heartbeat de {Server}: {Sessions} sessão(ões), {Procs} processo(s).",
            dto.ServerName, dto.Sessions.Count, dto.Processes.Count);
        return HeartbeatResult.Aceito;
    }

    private static string Truncar(string texto, int max) => texto.Length <= max ? texto : texto[..max];

    /// <summary>Texto opcional: vazio vira null; o resto é aparado e truncado.</summary>
    private static string? Opcional(string? texto, int max)
        => string.IsNullOrWhiteSpace(texto) ? null : Truncar(texto.Trim(), max);

    /// <summary>Limite de segurança de eventos aceitos por heartbeat.</summary>
    private const int MaxEventos = 1_000;

    /// <summary>
    /// Grava as desconexões/reconexões enviadas pelo agente, ignorando as já
    /// recebidas (o agente reenvia quando um heartbeat falha), e liga cada uma
    /// à sessão do histórico em que aconteceu.
    /// </summary>
    private async Task GravarEventosAsync(Server server, List<SessionEventDto> eventos, List<SessionHistory> abertos)
    {
        var lote = eventos.Take(MaxEventos)
            .Select(e => (Dto: e, Quando: DateTime.SpecifyKind(ParaUtc(e.TimeUtc), DateTimeKind.Unspecified)))
            .ToList();

        var ids = lote.Select(e => e.Dto.RecordId).Distinct().ToList();
        var jaGravados = (await _db.SessionEvents.AsNoTracking()
                .Where(e => e.ServerName == server.Name && ids.Contains(e.RecordId))
                .Select(e => new { e.RecordId, e.DataHora })
                .ToListAsync())
            .Select(e => (e.RecordId, e.DataHora))
            .ToHashSet();

        var novos = 0;
        foreach (var (e, quando) in lote)
        {
            if (!jaGravados.Add((e.RecordId, quando))) continue;   // duplicado (inclusive dentro do lote)

            var tipo = e.Type.Equals("Reconnect", StringComparison.OrdinalIgnoreCase) || e.EventId == 25
                ? SessionEventType.Reconexao
                : SessionEventType.Desconexao;
            var usuario = Opcional(e.Username, 150) ?? string.Empty;

            var historico = abertos
                .Where(h => h.RdsSessionId == e.SessionId
                         && h.LogonTime <= quando.AddMinutes(1)
                         && (h.LogoffTime == null || h.LogoffTime >= quando.AddMinutes(-1)))
                .OrderByDescending(h => h.LogonTime)
                .FirstOrDefault()
                ?? await _db.SessionHistories
                    .Where(h => h.ServerName == server.Name
                             && h.RdsSessionId == e.SessionId
                             && h.LogonTime <= quando.AddMinutes(1)
                             && (h.LogoffTime == null || h.LogoffTime >= quando.AddMinutes(-1)))
                    .OrderByDescending(h => h.LogonTime)
                    .FirstOrDefaultAsync();

            _db.SessionEvents.Add(new SessionEvent
            {
                ServerName     = server.Name,
                RecordId       = e.RecordId,
                Tipo           = tipo,
                DataHora       = quando,
                RdsSessionId   = e.SessionId,
                Username       = usuario,
                ClientIp       = Opcional(e.ClientIp, 64),
                MotivoCodigo   = tipo == SessionEventType.Desconexao ? e.ReasonCode : null,
                SessionHistory = historico,
            });
            novos++;

            // Reconexão traz o IP de onde o usuário voltou
            if (tipo == SessionEventType.Reconexao && historico is not null
                && !string.IsNullOrWhiteSpace(e.ClientIp) && !e.ClientIp.Equals("LOCAL", StringComparison.OrdinalIgnoreCase))
                historico.ClientIp = Opcional(e.ClientIp, 64);
        }

        if (novos > 0)
            _log.LogInformation("{Server}: {Qtd} desconexão(ões)/reconexão(ões) registrada(s).", server.Name, novos);
    }

    /// <summary>
    /// Converte o horário recebido do agente para UTC. Agentes atuais já
    /// enviam em UTC (sufixo "Z"); versões antigas enviavam a hora local do
    /// servidor RDS sem fuso — nesse caso assume-se o mesmo fuso do portal.
    /// </summary>
    private static DateTime ParaUtc(DateTime valor) => valor.Kind switch
    {
        DateTimeKind.Utc   => valor,
        DateTimeKind.Local => valor.ToUniversalTime(),
        _                  => DateTime.SpecifyKind(valor, DateTimeKind.Local).ToUniversalTime(),
    };

    private static SessionState ParseState(string raw)
        => raw.ToLower() switch
        {
            "active"                 => SessionState.Active,
            "disconnected" or "disc" => SessionState.Disconnected,
            "idle"                   => SessionState.Idle,
            _                        => SessionState.Active,
        };
}
