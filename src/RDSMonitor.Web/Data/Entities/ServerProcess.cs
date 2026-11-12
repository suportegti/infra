namespace RDSMonitor.Web.Data.Entities;

/// <summary>
/// Processo em execução numa sessão de usuário, conforme o último heartbeat
/// do agente. A lista de cada servidor é substituída por inteiro a cada
/// heartbeat — é uma "foto" do momento, não um histórico.
/// </summary>
public class ServerProcess
{
    public int Id { get; set; }

    public int ServerId { get; set; }
    public Server Server { get; set; } = null!;

    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Dono do processo (sem domínio). Nulo quando o Windows não informa.</summary>
    public string? Username { get; set; }

    /// <summary>ID da sessão RDS a que o processo pertence.</summary>
    public int RdsSessionId { get; set; }

    public double MemoryMb { get; set; }
    public double CpuPercent { get; set; }

    /// <summary>Processo de sistema/crítico: o portal não oferece "derrubar".</summary>
    public bool IsProtected { get; set; }

    public DateTime LastSeen { get; set; }
}
