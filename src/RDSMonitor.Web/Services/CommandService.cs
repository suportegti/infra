using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models.Dtos;

namespace RDSMonitor.Web.Services;

// ─────────────────────────────────────────────
//  Interface
// ─────────────────────────────────────────────

public interface ICommandService
{
    /// <summary>
    /// Cria e persiste um comando para o servidor indicado.
    /// Retorna o comando criado (com Id gerado pelo banco).
    /// </summary>
    Task<Command> CreateAsync(
        int    serverId,
        CommandType type,
        Dictionary<string, string> parameters,
        string createdByUsername);

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

    public CommandService(AppDbContext db, ILogger<CommandService> log)
    {
        _db  = db;
        _log = log;
    }

    public async Task<Command> CreateAsync(
        int serverId,
        CommandType type,
        Dictionary<string, string> parameters,
        string createdByUsername)
    {
        var command = new Command
        {
            ServerId            = serverId,
            Type                = type,
            ParametersJson      = JsonSerializer.Serialize(parameters),
            Status              = CommandStatus.Pending,
            CreatedByUsername   = createdByUsername,
        };

        _db.Commands.Add(command);
        await _db.SaveChangesAsync();

        _log.LogInformation("Comando {Type} enfileirado para servidor {ServerId} por {User}.",
            type, serverId, createdByUsername);

        return command;
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
            await _db.SaveChangesAsync();

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
