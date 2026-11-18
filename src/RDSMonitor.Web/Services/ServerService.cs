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
    private readonly IAtualizacaoAgenteService _atualizacao;
    private readonly ILogger<ServerService> _log;

    public ServerService(AppDbContext db, IAtualizacaoAgenteService atualizacao, ILogger<ServerService> log)
    {
        _db          = db;
        _atualizacao = atualizacao;
        _log         = log;
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

        // Versão do agente e conclusão/falha da atualização automática em andamento
        await _atualizacao.RegistrarHeartbeatAsync(server, dto.AgentVersion, dto.UpdateResult);

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
                // Já encerrado pelo evento 23 (logoff) num heartbeat anterior: nada a fazer
                var de = s.LogonTime.AddMinutes(-2);
                var ate = s.LogonTime.AddMinutes(2);
                var jaFechado = await _db.SessionHistories.AnyAsync(h =>
                    h.ServerName == server.Name && h.RdsSessionId == s.RdsSessionId && h.Username == s.Username
                    && h.LogoffTime != null && h.LogonTime >= de && h.LogonTime <= ate);
                if (jaFechado)
                {
                    _db.Sessions.Remove(s);
                    continue;
                }

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
            (aberto.LogoffReason, aberto.AdminUsername) = await EncerramentoDoLogoffAsync(server, s.RdsSessionId, agora);

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
                    anterior.LogoffReason  = StatusSessao.Logoff;
                    anterior.AdminUsername = null;
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

                // O evento 21 (logon) pode já ter aberto o registro desta sessão
                var adotado = abertos.FirstOrDefault(h => h.LogoffTime == null
                    && h.RdsSessionId == sessionDto.SessionId
                    && string.Equals(h.Username, sessionDto.Username, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs((h.LogonTime - sessionDto.LogonTime).TotalMinutes) <= 2);
                if (adotado is not null)
                {
                    adotado.ClientName ??= nomeCliente;
                    adotado.ClientIp   ??= ipCliente;
                }
                else
                {
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
                        LogoffReason = StatusSessao.Conectado,
                    };
                    _db.SessionHistories.Add(novo);
                    abertos.Add(novo);
                }
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

        // ── Registros abertos sem sessão viva no servidor ────
        // (ex.: o agente esteve fora durante o logoff, ou o ID foi reaproveitado)
        var vivas = dto.Sessions.Select(x => (x.SessionId, x.Username.ToLowerInvariant())).ToHashSet();
        foreach (var h in abertos.Where(h => h.LogoffTime == null
                                          && h.LogonTime < agora.AddMinutes(-2)
                                          && !vivas.Contains((h.RdsSessionId, h.Username.ToLowerInvariant()))).ToList())
        {
            h.LogoffTime   = agora;
            h.LogoffReason = StatusSessao.Logoff;
        }

        // ── Ciclo de vida das sessões (eventos do Windows) ───
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

    /// <summary>
    /// Como a sessão terminou: se um operador pediu Logoff pelo portal para esta
    /// sessão nos últimos minutos, foi ele; senão, logoff comum (usuário, política
    /// de tempo do RDS ou fora do portal).
    /// </summary>
    private async Task<(string Texto, string? Operador)> EncerramentoDoLogoffAsync(Server server, int rdsSessionId, DateTime agora)
    {
        var desde = agora.AddMinutes(-15);
        var operador = await _db.Auditoria.AsNoTracking()
            .Where(a => a.Acao == CommandType.Logoff
                     && a.ServidorNome == server.Name
                     && a.SessaoId == rdsSessionId
                     && a.DataHora >= desde
                     && a.Status != CommandStatus.Failed)
            .OrderByDescending(a => a.DataHora)
            .Select(a => a.OperadorLogin)
            .FirstOrDefaultAsync();

        return operador is null ? (StatusSessao.Logoff, null) : (StatusSessao.LogoffAdministrador, operador);
    }

    /// <summary>
    /// Disconnect pedido pelo portal que explica esta desconexão: mesmo servidor e
    /// sessão, pedido até 10 min antes, ainda não associado a outra desconexão.
    /// Queda de rede (código 0) nunca é atribuída a um operador.
    /// </summary>
    private async Task<AuditoriaAcao?> DisconnectDoPortalAsync(Server server, int rdsSessionId, DateTime quando, int? motivo, HashSet<long> usados)
    {
        if (motivo == 0) return null;
        var de  = quando.AddMinutes(-10);
        var ate = quando.AddMinutes(1);
        var candidatos = await _db.Auditoria.AsNoTracking()
            .Where(a => a.Acao == CommandType.Disconnect
                     && a.ServidorNome == server.Name
                     && a.SessaoId == rdsSessionId
                     && a.DataHora >= de && a.DataHora <= ate
                     && a.Status != CommandStatus.Failed
                     && !_db.SessionEvents.Any(x => x.AuditoriaId == a.Id))
            .OrderByDescending(a => a.DataHora)
            .ToListAsync();
        return candidatos.FirstOrDefault(a => !usados.Contains(a.Id));
    }

    /// <summary>Limite de segurança de eventos aceitos por heartbeat.</summary>
    private const int MaxEventos = 1_000;

    /// <summary>
    /// Grava os eventos do ciclo de vida enviados pelo agente (ignorando os já
    /// recebidos — o agente reenvia quando um heartbeat falha) e usa cada um para
    /// manter o histórico:
    ///  • 21 Logon abre o registro (mesmo de sessões curtas que o heartbeat não viu);
    ///  • 23 Logoff fecha no horário exato do Windows;
    ///  • 24/25 atualizam o Status; na desconexão, o 39 e a auditoria dizem quem foi;
    ///  • 1149 e 25 trazem o IP de origem.
    /// </summary>
    private async Task GravarEventosAsync(Server server, List<SessionEventDto> eventos, List<SessionHistory> abertos)
    {
        // Autenticações por último: assim o logon que vem logo depois já abriu o registro
        var lote = eventos.Take(MaxEventos)
            .Select(e => (Dto: e, Quando: DateTime.SpecifyKind(ParaUtc(e.TimeUtc), DateTimeKind.Unspecified)))
            .OrderBy(x => x.Dto.EventId == 1149 ? 1 : 0)
            .ThenBy(x => x.Quando)
            .ToList();

        var ids = lote.Select(e => e.Dto.RecordId).Distinct().ToList();
        var jaGravados = (await _db.SessionEvents.AsNoTracking()
                .Where(e => e.ServerName == server.Name && ids.Contains(e.RecordId))
                .Select(e => new { e.RecordId, e.DataHora })
                .ToListAsync())
            .Select(e => (e.RecordId, e.DataHora))
            .ToHashSet();

        var novos = 0;
        var auditoriasUsadas = new HashSet<long>();
        var ultimoPorHistorico = new Dictionary<SessionHistory, SessionEvent>();

        foreach (var (e, quando) in lote)
        {
            if (TipoDoEvento(e) is not { } tipo) continue;
            if (!jaGravados.Add((e.RecordId, quando))) continue;   // duplicado (inclusive dentro do lote)

            var usuario = Opcional(e.Username, 150) ?? string.Empty;
            var ip      = Opcional(e.ClientIp, 64);
            var ipReal  = ip is not null && !ip.Equals("LOCAL", StringComparison.OrdinalIgnoreCase) ? ip : null;

            var historico = tipo switch
            {
                SessionEventType.Autenticacao => await HistoricoDaAutenticacaoAsync(server, usuario, quando, abertos),
                SessionEventType.Logon        => await HistoricoDoLogonAsync(server, e.SessionId, usuario, quando, ipReal, abertos),
                _                             => await HistoricoDaSessaoAsync(server, e.SessionId, quando, abertos),
            };

            // Desconexão: quem pediu (portal > evento 39 > código do evento 40)
            int? motivo = null, porSessao = null;
            string? porUsuario = null;
            AuditoriaAcao? portal = null;
            if (tipo == SessionEventType.Desconexao)
            {
                motivo    = e.ReasonCode;
                porSessao = e.DisconnectedBySessionId;
                if (porSessao is null or 0)
                    portal = await DisconnectDoPortalAsync(server, e.SessionId, quando, motivo, auditoriasUsadas);
                if (portal is not null) auditoriasUsadas.Add(portal.Id);
                if (porSessao is > 0 && porSessao != e.SessionId)
                    porUsuario = UsuarioDaSessao(server, porSessao.Value, quando, abertos);
            }

            var evento = new SessionEvent
            {
                ServerName             = server.Name,
                RecordId               = e.RecordId,
                Tipo                   = tipo,
                DataHora               = quando,
                RdsSessionId           = e.SessionId,
                Username               = usuario,
                ClientIp               = ip,
                MotivoCodigo           = motivo,
                DesconectadoPorSessao  = porSessao,
                DesconectadoPorUsuario = porUsuario,
                Operador               = portal?.OperadorLogin,
                AuditoriaId            = portal?.Id,
                SessionHistory         = historico,
            };
            _db.SessionEvents.Add(evento);
            novos++;

            if (historico is null) continue;

            // Origem: logon, reconexão e autenticação informam o IP de onde o usuário veio
            if (ipReal is not null && tipo is SessionEventType.Logon or SessionEventType.Reconexao or SessionEventType.Autenticacao)
                historico.ClientIp = ipReal;

            if (tipo == SessionEventType.Logoff)
                await FecharPeloLogoffAsync(server, historico, quando);
            else if (StatusSessao.DeEvento(evento) is not null
                     && (!ultimoPorHistorico.TryGetValue(historico, out var anterior) || anterior.DataHora <= quando))
                ultimoPorHistorico[historico] = evento;
        }

        // Status das sessões abertas = último acontecimento da conexão
        // (só se for mais novo que os eventos já gravados — o agente pode reenviar atrasado)
        foreach (var (h, ultimo) in ultimoPorHistorico)
        {
            if (h.LogoffTime is not null) continue;
            if (h.Id > 0)
            {
                var maisRecente = await _db.SessionEvents.AsNoTracking()
                    .Where(x => x.SessionHistoryId == h.Id
                             && x.Tipo != SessionEventType.Shell && x.Tipo != SessionEventType.Autenticacao)
                    .MaxAsync(x => (DateTime?)x.DataHora);
                if (maisRecente is { } m && m > ultimo.DataHora) continue;
            }
            var (texto, quem) = StatusSessao.DeEvento(ultimo)!.Value;
            h.LogoffReason  = texto;
            h.AdminUsername = quem;
        }

        if (novos > 0)
            _log.LogInformation("{Server}: {Qtd} evento(s) de sessão registrado(s).", server.Name, novos);
    }

    private static SessionEventType? TipoDoEvento(SessionEventDto e) => e.EventId switch
    {
        1149 => SessionEventType.Autenticacao,
        21   => SessionEventType.Logon,
        22   => SessionEventType.Shell,
        23   => SessionEventType.Logoff,
        24   => SessionEventType.Desconexao,
        25   => SessionEventType.Reconexao,
        // agentes 1.5.0 mandavam só 24/25 com o tipo em texto
        _ => e.Type.ToLowerInvariant() switch
        {
            "disconnect" => SessionEventType.Desconexao,
            "reconnect"  => SessionEventType.Reconexao,
            "logon"      => SessionEventType.Logon,
            "logoff"     => SessionEventType.Logoff,
            "shell"      => SessionEventType.Shell,
            "auth"       => SessionEventType.Autenticacao,
            _            => null,
        },
    };

    /// <summary>Registro da sessão em que o evento aconteceu (aberto ou que cobria aquele momento).</summary>
    private async Task<SessionHistory?> HistoricoDaSessaoAsync(Server server, int rdsSessionId, DateTime quando, List<SessionHistory> abertos)
    {
        var limiteLogon  = quando.AddMinutes(1);
        var limiteLogoff = quando.AddMinutes(-1);
        return abertos
                   .Where(h => h.RdsSessionId == rdsSessionId && h.LogonTime <= limiteLogon
                            && (h.LogoffTime == null || h.LogoffTime >= limiteLogoff))
                   .OrderByDescending(h => h.LogonTime)
                   .FirstOrDefault()
               ?? await _db.SessionHistories
                   .Where(h => h.ServerName == server.Name && h.RdsSessionId == rdsSessionId
                            && h.LogonTime <= limiteLogon
                            && (h.LogoffTime == null || h.LogoffTime >= limiteLogoff))
                   .OrderByDescending(h => h.LogonTime)
                   .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Evento 21: liga ao registro da sessão (criado pelo heartbeat com o mesmo
    /// horário de logon) ou abre um novo — assim sessões curtas, que começaram e
    /// terminaram entre dois heartbeats, também entram no histórico.
    /// </summary>
    private async Task<SessionHistory> HistoricoDoLogonAsync(Server server, int rdsSessionId, string usuario,
        DateTime quando, string? ip, List<SessionHistory> abertos)
    {
        var u   = usuario.ToLower();
        var de  = quando.AddMinutes(-2);
        var ate = quando.AddMinutes(2);

        var existente = abertos
                .Where(h => h.RdsSessionId == rdsSessionId && h.Username.ToLower() == u
                         && h.LogonTime >= de && h.LogonTime <= ate)
                .FirstOrDefault()
            ?? await _db.SessionHistories
                .Where(h => h.ServerName == server.Name && h.RdsSessionId == rdsSessionId
                         && h.Username.ToLower() == u && h.LogonTime >= de && h.LogonTime <= ate)
                .FirstOrDefaultAsync();
        if (existente is not null) return existente;

        // Registro do mesmo usuário que já cobria aquele momento (horário de logon um pouco diferente)
        var cobre = await HistoricoDaSessaoAsync(server, rdsSessionId, quando, abertos);
        if (cobre is not null && cobre.Username.ToLower() == u) return cobre;

        var novo = new SessionHistory
        {
            ServerName   = server.Name,
            RdsSessionId = rdsSessionId,
            Username     = usuario,
            LogonTime    = quando,
            Cliente      = server.Cliente,
            ClientIp     = ip,
            LogoffReason = StatusSessao.Conectado,
        };
        _db.SessionHistories.Add(novo);
        abertos.Add(novo);
        return novo;
    }

    /// <summary>
    /// Evento 1149 (antes da sessão existir ou da reconexão): sessão do mesmo
    /// usuário que estava aberta ou começou logo depois.
    /// </summary>
    private async Task<SessionHistory?> HistoricoDaAutenticacaoAsync(Server server, string usuario, DateTime quando, List<SessionHistory> abertos)
    {
        var u   = usuario.ToLower();
        var ate = quando.AddMinutes(3);
        return abertos
                   .Where(h => h.Username.ToLower() == u && h.LogonTime <= ate
                            && (h.LogoffTime == null || h.LogoffTime >= quando))
                   .OrderByDescending(h => h.LogonTime)
                   .FirstOrDefault()
               ?? await _db.SessionHistories
                   .Where(h => h.ServerName == server.Name && h.Username.ToLower() == u
                            && h.LogonTime <= ate && (h.LogoffTime == null || h.LogoffTime >= quando))
                   .OrderByDescending(h => h.LogonTime)
                   .FirstOrDefaultAsync();
    }

    /// <summary>Usuário de outra sessão do servidor (quem pediu a desconexão, evento 39).</summary>
    private static string? UsuarioDaSessao(Server server, int rdsSessionId, DateTime quando, List<SessionHistory> abertos)
        => server.Sessions.FirstOrDefault(s => s.RdsSessionId == rdsSessionId)?.Username
           ?? abertos.Where(h => h.RdsSessionId == rdsSessionId && h.LogonTime <= quando
                              && (h.LogoffTime == null || h.LogoffTime >= quando))
                     .Select(h => h.Username).FirstOrDefault();

    /// <summary>Evento 23: fecha o registro no horário exato do logoff informado pelo Windows.</summary>
    private async Task FecharPeloLogoffAsync(Server server, SessionHistory h, DateTime quando)
    {
        if (quando < h.LogonTime) return;
        if (h.LogoffTime is null)
        {
            h.LogoffTime = quando;
            (h.LogoffReason, h.AdminUsername) = await EncerramentoDoLogoffAsync(server, h.RdsSessionId, quando);
        }
        else if (quando < h.LogoffTime)
        {
            // O heartbeat percebeu a saída depois; vale o horário do Windows
            h.LogoffTime = quando;
        }
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
