namespace RDSMonitor.Web.Models.Dtos;

// ─────────────────────────────────────────────
//  Heartbeat: Agente → WebServer
// ─────────────────────────────────────────────

/// <summary>
/// Payload enviado pelo agente a cada ciclo (POST /api/agent/heartbeat).
/// Contém o estado completo do servidor naquele instante.
/// </summary>
public class AgentHeartbeatDto
{
    public string ServerName { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>CPU total do servidor em %.</summary>
    public double CpuPercent { get; set; }
    public double MemoryUsedGb { get; set; }
    public double MemoryTotalGb { get; set; }

    public List<SessionInfoDto> Sessions { get; set; } = new();
    public List<ProcessInfoDto> Processes { get; set; } = new();

    /// <summary>Desconexões/reconexões do log do Windows (agente 1.5.0 ou mais novo).</summary>
    public List<SessionEventDto> Events { get; set; } = new();
}

public class SessionEventDto
{
    public long RecordId { get; set; }
    public int EventId { get; set; }
    /// <summary>"Disconnect" ou "Reconnect".</summary>
    public string Type { get; set; } = string.Empty;
    public int SessionId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? ClientIp { get; set; }
    public DateTime TimeUtc { get; set; }
    public int? ReasonCode { get; set; }
}

public class SessionInfoDto
{
    /// <summary>ID RDS da sessão (ex: 15).</summary>
    public int SessionId { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>Nome lógico da sessão (ex: "rdp-tcp#15"). Vazio em sessões desconectadas.</summary>
    public string? SessionName { get; set; }

    /// <summary>"Active", "Disconnected" ou "Idle".</summary>
    public string State { get; set; } = "Active";

    public DateTime LogonTime { get; set; }
    public int IdleTimeMinutes { get; set; }

    /// <summary>CPU agregada de todos os processos do usuário nessa sessão.</summary>
    public double CpuPercent { get; set; }

    /// <summary>Memória total (working set) dos processos do usuário, em MB.</summary>
    public double MemoryMb { get; set; }

    /// <summary>Máquina e IP de onde o usuário conectou (agente 1.5.0 ou mais novo).</summary>
    public string? ClientName { get; set; }
    public string? ClientIp { get; set; }
}

public class ProcessInfoDto
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Username { get; set; }
    public int RdsSessionId { get; set; }
    public double MemoryMb { get; set; }

    /// <summary>CPU do processo em % do servidor inteiro.</summary>
    public double CpuPercent { get; set; }

    /// <summary>Processo de sistema/crítico — o portal não oferece "derrubar".</summary>
    public bool IsProtected { get; set; }
}

// ─────────────────────────────────────────────
//  Comandos: WebServer → Agente
// ─────────────────────────────────────────────

/// <summary>
/// Comando retornado ao agente pelo endpoint GET /api/agent/commands/{serverName}.
/// </summary>
public class CommandDto
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;

    /// <summary>Parâmetros já desserializados para facilitar uso no agente.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Resultado de execução enviado pelo agente (POST /api/agent/commands/{id}/result).
/// </summary>
public class CommandResultDto
{
    public bool Success { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
}
