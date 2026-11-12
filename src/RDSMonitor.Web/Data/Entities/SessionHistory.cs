namespace RDSMonitor.Web.Data.Entities;

/// <summary>
/// Histórico imutável de cada sessão RDS. Um registro é gravado quando
/// uma sessão aparece pela primeira vez no heartbeat (LogoffTime = null),
/// e o campo LogoffTime é preenchido quando o agente para de reportá-la.
/// </summary>
public class SessionHistory
{
    public int Id { get; set; }

    public string ServerName { get; set; } = string.Empty;
    public int RdsSessionId { get; set; }
    public string Username { get; set; } = string.Empty;
    public DateTime LogonTime { get; set; }
    public DateTime? LogoffTime { get; set; }

    /// <summary>
    /// Origem do encerramento: "Agente" (detectado pelo heartbeat),
    /// "Admin" (logoff forçado pelo portal) ou "Timeout".
    /// </summary>
    public string? LogoffReason { get; set; }

    /// <summary>Usuário do portal que ordenou o logoff, quando LogoffReason = "Admin".</summary>
    public string? AdminUsername { get; set; }
}
