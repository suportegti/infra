namespace RDSMonitor.Web.Data.Entities;

/// <summary>
/// Sessão RDS ativa em um servidor. Atualizada a cada heartbeat do agente.
/// Quando o agente deixa de reportar a sessão, ela é removida desta tabela
/// e um registro de encerramento é gravado em SessionHistory.
/// </summary>
public class Session
{
    public int Id { get; set; }

    public int ServerId { get; set; }
    public Server Server { get; set; } = null!;

    /// <summary>ID da sessão RDS no Windows. Ex: 15, 20, 31.</summary>
    public int RdsSessionId { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary>Nome lógico da sessão. Ex: "rdp-tcp#15".</summary>
    public string? SessionName { get; set; }

    public SessionState State { get; set; }
    public DateTime LogonTime { get; set; }
    public int IdleTimeMinutes { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
    public DateTime LastSeen { get; set; }

    /// <summary>Nome da máquina de onde o usuário conectou (último conhecido).</summary>
    public string? ClientName { get; set; }

    /// <summary>IP da máquina de onde o usuário conectou (último conhecido).</summary>
    public string? ClientIp { get; set; }
}

public enum SessionState
{
    Active,
    Disconnected,
    Idle,
}
