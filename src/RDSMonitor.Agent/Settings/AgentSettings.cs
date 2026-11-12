namespace RDSMonitor.Agent.Settings;

/// <summary>
/// Configurações do agente, lidas de appsettings.json → seção "AgentSettings".
/// Todos os campos obrigatórios são verificados na inicialização (Program.cs).
/// </summary>
public class AgentSettings
{
    /// <summary>
    /// Nome do servidor conforme cadastrado no portal (ex: "RDS01").
    /// Deve ser único — é a chave de identificação do agente na API.
    /// </summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>URL base do WebServer. Ex: "https://rdsmonitor.empresa.local".</summary>
    public string WebApiUrl { get; set; } = string.Empty;

    /// <summary>
    /// Chave compartilhada entre o agente e o WebServer (header X-Agent-Key).
    /// Deve ser idêntica ao valor de AgentSettings:ApiKey no appsettings.json do Web.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Intervalo de envio do heartbeat em segundos. Padrão: 30.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>Intervalo de busca de comandos pendentes em segundos. Padrão: 10.</summary>
    public int CommandPollIntervalSeconds { get; set; } = 10;

    /// <summary>Modo de simulação para testes em máquina de desenvolvimento.</summary>
    public SimulationSettings Simulacao { get; set; } = new();
}

/// <summary>
/// Modo de simulação (seção "AgentSettings:Simulacao"). Quando habilitado:
///  • o agente acrescenta sessões FICTÍCIAS (com processos fictícios) ao heartbeat;
///  • Disconnect, Logoff e Derrubar processo nessas sessões são simulados de
///    verdade no agente (estado muda, sessão some, histórico fecha no portal);
///  • Logoff, Disconnect e reinícios nas sessões REAIS da máquina são
///    bloqueados — assim dá para testar sem derrubar a própria sessão.
/// NUNCA habilitar em servidores RDS de produção.
/// </summary>
public class SimulationSettings
{
    public bool Habilitada { get; set; }

    /// <summary>Usuários das sessões fictícias. Vazio = ana.teste, bruno.teste, carla.teste.</summary>
    public List<string> Usuarios { get; set; } = new();

    /// <summary>Segundos até o usuário fictício "fazer login de novo" depois de um logoff. Padrão: 90.</summary>
    public int RelogarAposSegundos { get; set; } = 90;
}
