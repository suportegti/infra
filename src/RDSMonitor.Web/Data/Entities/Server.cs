namespace RDSMonitor.Web.Data.Entities;

/// <summary>
/// Representa um servidor RDS monitorado. Cadastrado pelo Administrador na
/// tela Servidores; o agente só é aceito se o nome configurado nele
/// (AgentSettings:ServerName) corresponder a um servidor cadastrado e habilitado.
/// </summary>
public class Server
{
    public int Id { get; set; }

    /// <summary>Nome do servidor, conforme configurado no agente. Ex: "RDS01".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Nome amigável opcional, editável no portal.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Cliente (empresa) atendido pelo servidor. Usado como filtro em Sessões e Relatórios.</summary>
    public string? Cliente { get; set; }

    /// <summary>Verdadeiro se o último heartbeat chegou há menos de 2 minutos.</summary>
    public bool IsOnline { get; set; }

    public DateTime? LastHeartbeat { get; set; }

    public double CpuPercent { get; set; }
    public double MemoryUsedGb { get; set; }
    public double MemoryTotalGb { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Session> Sessions { get; set; } = new List<Session>();
    public ICollection<Command> Commands { get; set; } = new List<Command>();
    public ICollection<ServerProcess> Processes { get; set; } = new List<ServerProcess>();
}
