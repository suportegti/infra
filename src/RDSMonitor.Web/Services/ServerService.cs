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
    Task<ServerOperationResult> CreateAsync(string name, string? displayName);
    Task<ServerOperationResult> UpdateAsync(int id, string name, string? displayName);
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

    public async Task<ServerOperationResult> CreateAsync(string name, string? displayName)
    {
        name = name?.Trim() ?? string.Empty;
        if (!NomeValido.IsMatch(name)) return ServerOperationResult.NomeInvalido;

        if (await NomeEmUsoAsync(name, ignorarId: null)) return ServerOperationResult.NomeJaExiste;

        _db.Servers.Add(new Server
        {
            Name        = name,
            DisplayName = Limpar(displayName),
            IsEnabled   = true,
        });
        await _db.SaveChangesAsync();
        _log.LogInformation("Servidor {Name} cadastrado.", name);
        return ServerOperationResult.Sucesso;
    }

    public async Task<ServerOperationResult> UpdateAsync(int id, string name, string? displayName)
    {
        var server = await _db.Servers.FindAsync(id);
        if (server is null) return ServerOperationResult.NaoEncontrado;

        name = name?.Trim() ?? string.Empty;
        if (!NomeValido.IsMatch(name)) return ServerOperationResult.NomeInvalido;
        if (await NomeEmUsoAsync(name, ignorarId: id)) return ServerOperationResult.NomeJaExiste;

        server.Name        = name;
        server.DisplayName = Limpar(displayName);
        await _db.SaveChangesAsync();
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

        // ── Sessões que sumiram → fechar histórico e remover ─
        var reportedIds = dto.Sessions.Select(s => s.SessionId).ToHashSet();
        var removed     = server.Sessions.Where(s => !reportedIds.Contains(s.RdsSessionId)).ToList();

        foreach (var s in removed)
        {
            // Fecha o registro de histórico aberto quando a sessão apareceu.
            // Só cria um novo (já fechado) se não houver registro aberto —
            // ex.: sessão que existia antes de o histórico ser implantado.
            var aberto = await _db.SessionHistories
                .Where(h => h.ServerName   == server.Name
                         && h.RdsSessionId == s.RdsSessionId
                         && h.Username     == s.Username
                         && h.LogoffTime   == null)
                .OrderByDescending(h => h.Id)
                .FirstOrDefaultAsync();

            if (aberto is null)
            {
                aberto = new SessionHistory
                {
                    ServerName   = server.Name,
                    RdsSessionId = s.RdsSessionId,
                    Username     = s.Username,
                    LogonTime    = s.LogonTime,
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

            if (existing is null)
            {
                existing = new Session
                {
                    ServerId     = server.Id,
                    RdsSessionId = sessionDto.SessionId,
                    LogonTime    = sessionDto.LogonTime,
                };
                _db.Sessions.Add(existing);

                // Inicia o registro de histórico (LogoffTime preenchido depois)
                _db.SessionHistories.Add(new SessionHistory
                {
                    ServerName   = server.Name,
                    RdsSessionId = sessionDto.SessionId,
                    Username     = sessionDto.Username,
                    LogonTime    = sessionDto.LogonTime,
                });
            }

            existing.Username        = sessionDto.Username;
            existing.SessionName     = sessionDto.SessionName;
            existing.State           = ParseState(sessionDto.State);
            existing.LogonTime       = sessionDto.LogonTime;
            existing.IdleTimeMinutes = sessionDto.IdleTimeMinutes;
            existing.CpuPercent      = Math.Round(sessionDto.CpuPercent, 1);
            existing.MemoryMb        = Math.Round(sessionDto.MemoryMb, 1);
            existing.LastSeen        = agora;
        }

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
