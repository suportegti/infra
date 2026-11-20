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

    /// <summary>
    /// Último contato do agente (heartbeat a cada 30 s ou busca de comandos a cada 3 s).
    /// Sem contato por 20 s o servidor fica offline (ServerHealthMonitor).
    /// </summary>
    public DateTime? UltimoContato { get; set; }

    public double CpuPercent { get; set; }
    public double MemoryUsedGb { get; set; }
    public double MemoryTotalGb { get; set; }

    public bool IsEnabled { get; set; } = true;

    // ── Agente e atualização automática ──────────────────────
    /// <summary>Versão informada pelo agente no heartbeat (ex.: "1.7.0.0"; vazio = agente antigo).</summary>
    public string? AgentVersion { get; set; }

    /// <summary>Versão para a qual o agente deve se atualizar (null = nada pendente).</summary>
    public string? VersaoAlvo { get; set; }

    /// <summary>Situação da última atualização (ver SituacaoAtualizacao) e detalhe.</summary>
    public string? AtualizacaoStatus { get; set; }
    public string? AtualizacaoMensagem { get; set; }

    /// <summary>Última mudança na atualização (pedido, conclusão ou falha), UTC.</summary>
    public DateTime? AtualizacaoEm { get; set; }

    /// <summary>Quando a instrução foi entregue ao agente (conta o tempo limite), UTC.</summary>
    public DateTime? AtualizacaoEnviadaEm { get; set; }

    /// <summary>Registro de auditoria do pedido em andamento.</summary>
    public long? AtualizacaoAuditoriaId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Session> Sessions { get; set; } = new List<Session>();
    public ICollection<Command> Commands { get; set; } = new List<Command>();
    public ICollection<ServerProcess> Processes { get; set; } = new List<ServerProcess>();
}
