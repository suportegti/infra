using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models.Dtos;
using RDSMonitor.Web.Security;

namespace RDSMonitor.Web.Services;

// ─────────────────────────────────────────────
//  Interface
// ─────────────────────────────────────────────

public interface ICommandService
{
    /// <summary>
    /// Cria o comando para o servidor indicado e grava o registro de auditoria.
    /// Confere no servidor se o operador tem a permissão da ação — lança
    /// <see cref="UnauthorizedAccessException"/> se não tiver.
    /// Retorna o comando criado (com Id gerado pelo banco).
    /// </summary>
    Task<Command> CreateAsync(
        int    serverId,
        CommandType type,
        Dictionary<string, string> parameters,
        ClaimsPrincipal operador);

    /// <summary>
    /// Retorna os comandos pendentes de um servidor para o agente executar.
    /// Marca-os como "Executing" para não retorná-los duas vezes.
    /// </summary>
    Task<List<CommandDto>> GetPendingForServerAsync(string serverName);

    /// <summary>Registra o resultado de execução reportado pelo agente.</summary>
    Task UpdateResultAsync(int commandId, bool success, string? output, string? error);

    /// <summary>Retorna os últimos N comandos de um servidor para exibição no portal.</summary>
    Task<List<Command>> GetRecentAsync(int serverId, int take = 20);

    /// <summary>Estado atual dos comandos indicados (sem rastreamento).</summary>
    Task<List<Command>> GetByIdsAsync(IEnumerable<int> ids);
}

// ─────────────────────────────────────────────
//  Implementação
// ─────────────────────────────────────────────

public class CommandService : ICommandService
{
    private readonly AppDbContext _db;
    private readonly ILogger<CommandService> _log;
    private readonly IPermissaoService _permissoes;

    public CommandService(AppDbContext db, ILogger<CommandService> log, IPermissaoService permissoes)
    {
        _db  = db;
        _log = log;
        _permissoes = permissoes;
    }

    /// <summary>Permissão exigida para cada tipo de comando.</summary>
    public static string PermissaoDe(CommandType tipo) => tipo switch
    {
        CommandType.Logoff         => Permissoes.SessoesLogoff,
        CommandType.Disconnect     => Permissoes.SessoesDesconectar,
        CommandType.KillProcess    => Permissoes.ProcessosDerrubar,
        CommandType.SendMessage    => Permissoes.SessoesMensagem,
        CommandType.RestartService => Permissoes.ServidoresReiniciar,
        CommandType.RestartServer  => Permissoes.ServidoresReiniciar,
        _                          => Permissoes.ComandosManuais,
    };

    public async Task<Command> CreateAsync(
        int serverId,
        CommandType type,
        Dictionary<string, string> parameters,
        ClaimsPrincipal operador)
    {
        var permissao = PermissaoDe(type);
        if (!await _permissoes.PossuiAsync(operador, permissao))
            throw new UnauthorizedAccessException(
                $"Seu perfil não tem a permissão \"{Permissoes.NomeDe(permissao)}\".");

        var login  = operador.FindFirstValue("username") ?? operador.Identity?.Name ?? "portal";
        var nome   = operador.Identity?.Name;
        int? userId = int.TryParse(operador.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;

        var command = new Command
        {
            ServerId            = serverId,
            Type                = type,
            ParametersJson      = JsonSerializer.Serialize(parameters),
            Status              = CommandStatus.Pending,
            CreatedByUserId     = userId,
            CreatedByUsername   = nome ?? login,
        };

        _db.Commands.Add(command);
        await _db.SaveChangesAsync();

        _db.Auditoria.Add(await MontarAuditoriaAsync(command, parameters, login, nome, operador.FindFirstValue(ClaimTypes.Role)));
        await _db.SaveChangesAsync();

        _log.LogInformation("Comando {Type} enfileirado para servidor {ServerId} por {User}.",
            type, serverId, login);

        return command;
    }

    /// <summary>
    /// Registro de auditoria com o "retrato" do alvo no momento da ação
    /// (usuário da sessão, nome do processo), já que sessões e processos mudam.
    /// </summary>
    private async Task<AuditoriaAcao> MontarAuditoriaAsync(
        Command cmd, Dictionary<string, string> p, string login, string? nome, string? perfil)
    {
        int? sessao = p.TryGetValue("SessionId", out var s) && int.TryParse(s, out var si) ? si : null;
        int? pid    = p.TryGetValue("Pid", out var x) && int.TryParse(x, out var px) ? px : null;
        string? processo = p.GetValueOrDefault("ProcessName");

        var servidor = await _db.Servers.AsNoTracking().Where(v => v.Id == cmd.ServerId)
                                        .Select(v => v.Name).FirstOrDefaultAsync() ?? $"#{cmd.ServerId}";

        if (pid is not null)
        {
            var proc = await _db.Processes.AsNoTracking()
                .Where(pr => pr.ServerId == cmd.ServerId && pr.Pid == pid)
                .Select(pr => new { pr.Name, pr.RdsSessionId }).FirstOrDefaultAsync();
            processo ??= proc?.Name;
            sessao   ??= proc?.RdsSessionId;
        }

        string? usuarioAlvo = null;
        if (sessao is not null)
            usuarioAlvo = await _db.Sessions.AsNoTracking()
                .Where(se => se.ServerId == cmd.ServerId && se.RdsSessionId == sessao)
                .Select(se => se.Username).FirstOrDefaultAsync();

        var detalhes = cmd.Type switch
        {
            CommandType.SendMessage    => "Mensagem: " + p.GetValueOrDefault("Message"),
            CommandType.RestartService => "Serviço: " + (p.GetValueOrDefault("ServiceName") ?? "TermService"),
            CommandType.RestartServer  => $"Reinício em {p.GetValueOrDefault("DelaySeconds") ?? "30"} s",
            _                          => null,
        };

        return new AuditoriaAcao
        {
            DataHora       = cmd.CreatedAt,
            OperadorLogin  = login,
            OperadorNome   = nome,
            OperadorPerfil = perfil,
            Acao           = cmd.Type,
            ServidorId     = cmd.ServerId,
            ServidorNome   = servidor,
            SessaoId       = sessao,
            UsuarioAlvo    = usuarioAlvo,
            Processo       = processo,
            Pid            = pid,
            Detalhes       = detalhes is { Length: > 1000 } ? detalhes[..1000] : detalhes,
            CommandId      = cmd.Id,
            Status         = CommandStatus.Pending,
        };
    }

    public async Task<List<CommandDto>> GetPendingForServerAsync(string serverName)
    {
        var server = await _db.Servers
            .FirstOrDefaultAsync(s => s.Name.ToLower() == serverName.ToLower());

        // Servidor não cadastrado ou desabilitado não recebe comandos.
        if (server is null || !server.IsEnabled) return new();

        var pendentes = await _db.Commands
            .Where(c => c.ServerId == server.Id && c.Status == CommandStatus.Pending)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

        // Marca como "Executing" para não retornar na próxima chamada
        foreach (var cmd in pendentes)
            cmd.Status = CommandStatus.Executing;

        if (pendentes.Count > 0)
        {
            await _db.SaveChangesAsync();
            var ids = pendentes.Select(c => (int?)c.Id).ToList();
            await _db.Auditoria.Where(a => ids.Contains(a.CommandId))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, CommandStatus.Executing));
        }

        return pendentes.Select(c => new CommandDto
        {
            Id         = c.Id,
            Type       = c.Type.ToString(),
            Parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(c.ParametersJson)
                         ?? new(),
            CreatedAt  = c.CreatedAt,
        }).ToList();
    }

    public async Task UpdateResultAsync(int commandId, bool success, string? output, string? error)
    {
        var command = await _db.Commands.FindAsync(commandId);
        if (command is null) return;

        command.Status      = success ? CommandStatus.Completed : CommandStatus.Failed;
        command.ExecutedAt  = DateTime.UtcNow;
        command.Output      = output;
        command.Error       = error;

        // Resultado também no registro de auditoria
        var resultado = success ? output : error;
        await _db.Auditoria.Where(a => a.CommandId == commandId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(a => a.Status, command.Status)
                .SetProperty(a => a.ConcluidoEm, command.ExecutedAt)
                .SetProperty(a => a.Resultado, resultado != null && resultado.Length > 2000 ? resultado.Substring(0, 2000) : resultado));

        await _db.SaveChangesAsync();

        _log.LogInformation("Comando {Id} finalizado: {Status}. Output: {Output}",
            commandId, command.Status, output ?? error);
    }

    public Task<List<Command>> GetByIdsAsync(IEnumerable<int> ids)
    {
        var lista = ids.ToList();
        return _db.Commands.AsNoTracking().Where(c => lista.Contains(c.Id)).ToListAsync();
    }

    public Task<List<Command>> GetRecentAsync(int serverId, int take = 20)
        => _db.Commands
              .Where(c => c.ServerId == serverId)
              .OrderByDescending(c => c.CreatedAt)
              .Take(take)
              .ToListAsync();
}
