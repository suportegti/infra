namespace RDSMonitor.Web.Data.Entities;

/// <summary>
/// Pacote do agente (RDSMonitor.Agent-x.y.z.zip) enviado ao portal na tela
/// Atualização de agentes. O arquivo fica em data\pacotes-agente; os agentes
/// baixam dele quando o portal pede a atualização.
/// </summary>
public class AgentePacote
{
    public int Id { get; set; }

    /// <summary>Versão do RDSMonitor.Agent.dll do pacote, normalizada (ex.: "1.7.0.0").</summary>
    public string Versao { get; set; } = string.Empty;

    /// <summary>Nome do arquivo em data\pacotes-agente.</summary>
    public string NomeArquivo { get; set; } = string.Empty;

    /// <summary>Nome do arquivo como foi enviado.</summary>
    public string? NomeOriginal { get; set; }

    public long TamanhoBytes { get; set; }

    /// <summary>SHA-256 do .zip (hexadecimal) — o agente confere antes de instalar.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public DateTime EnviadoEm { get; set; } = DateTime.UtcNow;
    public string? EnviadoPor { get; set; }
}
