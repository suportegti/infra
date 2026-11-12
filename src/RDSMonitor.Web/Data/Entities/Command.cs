namespace RDSMonitor.Web.Data.Entities;

/// <summary>
/// Comando enfileirado pelo portal para ser executado pelo agente
/// no próximo ciclo de polling. O agente busca os comandos com
/// Status = Pending, executa e atualiza o resultado.
/// </summary>
public class Command
{
    public int Id { get; set; }

    public int ServerId { get; set; }
    public Server Server { get; set; } = null!;

    public CommandType Type { get; set; }

    /// <summary>Parâmetros serializados em JSON. Ex: {"SessionId":"15"}.</summary>
    public string ParametersJson { get; set; } = "{}";

    public CommandStatus Status { get; set; } = CommandStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExecutedAt { get; set; }

    public string? Output { get; set; }
    public string? Error { get; set; }

    public int? CreatedByUserId { get; set; }
    public string? CreatedByUsername { get; set; }
}

public enum CommandType
{
    Logoff,
    Disconnect,
    KillProcess,
    SendMessage,
    RestartService,
    RestartServer,
}

public enum CommandStatus
{
    Pending,
    Executing,
    Completed,
    Failed,
}
