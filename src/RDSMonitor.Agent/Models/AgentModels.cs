namespace RDSMonitor.Agent.Models;

// ─────────────────────────────────────────────
//  Enviados pelo agente ao WebServer
// ─────────────────────────────────────────────

public class AgentHeartbeat
{
    public string ServerName    { get; set; } = string.Empty;
    public DateTime Timestamp   { get; set; } = DateTime.UtcNow;
    public double CpuPercent    { get; set; }
    public double MemoryUsedGb  { get; set; }
    public double MemoryTotalGb { get; set; }
    public List<SessionInfo> Sessions  { get; set; } = new();
    public List<ProcessInfo> Processes { get; set; } = new();

    /// <summary>Desconexões e reconexões lidas do log do Windows desde o último envio.</summary>
    public List<SessionEventInfo> Events { get; set; } = new();
}

/// <summary>
/// Evento do ciclo de vida da sessão lido dos logs do Windows: 1149 autenticação,
/// 21 logon, 22 shell, 23 logoff, 24 desconexão, 25 reconexão. Numa desconexão,
/// o motivo vem do evento 40 e a sessão que a pediu, do evento 39.
/// </summary>
public class SessionEventInfo
{
    /// <summary>Log do Windows de onde veio o evento (o RecordId é sequencial por log).</summary>
    public string Log           { get; set; } = string.Empty;

    /// <summary>EventRecordID do Windows — o portal usa para não gravar o mesmo evento duas vezes.</summary>
    public long RecordId        { get; set; }
    public int EventId          { get; set; }
    /// <summary>"Auth", "Logon", "Shell", "Logoff", "Disconnect" ou "Reconnect".</summary>
    public string Type          { get; set; } = string.Empty;
    public int SessionId        { get; set; }
    public string Username      { get; set; } = string.Empty;
    /// <summary>Endereço de origem informado pelo Windows (IP ou "LOCAL").</summary>
    public string? ClientIp     { get; set; }
    public DateTime TimeUtc     { get; set; }
    /// <summary>Código de motivo do evento 40 (só em desconexões).</summary>
    public int? ReasonCode      { get; set; }
    /// <summary>Sessão que pediu a desconexão (evento 39): a própria = o usuário; outra = alguém nela; 0 = serviço do servidor.</summary>
    public int? DisconnectedBySessionId { get; set; }
}

public class SessionInfo
{
    public int SessionId        { get; set; }
    public string Username      { get; set; } = string.Empty;
    public string? SessionName  { get; set; }
    public string State         { get; set; } = "Active";
    /// <summary>Horário de logon em UTC.</summary>
    public DateTime LogonTime   { get; set; }
    public int IdleTimeMinutes  { get; set; }
    public double CpuPercent    { get; set; }
    public double MemoryMb      { get; set; }
    /// <summary>Nome da máquina de onde o usuário conectou (WTSClientName).</summary>
    public string? ClientName   { get; set; }
    /// <summary>IP da máquina de onde o usuário conectou (WTSClientAddress).</summary>
    public string? ClientIp     { get; set; }
}

public class ProcessInfo
{
    public int Pid              { get; set; }
    public string Name          { get; set; } = string.Empty;
    public string? Username     { get; set; }
    public int RdsSessionId     { get; set; }
    public double MemoryMb      { get; set; }

    /// <summary>CPU do processo em % do servidor inteiro (0 na primeira coleta).</summary>
    public double CpuPercent    { get; set; }

    /// <summary>Processo de sistema/crítico — o portal não oferece "derrubar".</summary>
    public bool IsProtected     { get; set; }
}

// ─────────────────────────────────────────────
//  Recebidos do WebServer pelo agente
// ─────────────────────────────────────────────

public class CommandDto
{
    public int Id                                    { get; set; }
    public string Type                               { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters     { get; set; } = new();
    public DateTime CreatedAt                        { get; set; }
}

public class CommandResult
{
    public bool Success     { get; set; }
    public string? Output   { get; set; }
    public string? Error    { get; set; }
}
