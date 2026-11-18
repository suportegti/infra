namespace RDSMonitor.Web.Data.Entities;

public enum SessionEventType
{
    /// <summary>Evento 24 do Windows: sessão desconectada.</summary>
    Desconexao,
    /// <summary>Evento 25 do Windows: reconexão à sessão.</summary>
    Reconexao,
}

/// <summary>
/// Desconexão ou reconexão de uma sessão RDS, lida pelo agente do log
/// "TerminalServices-LocalSessionManager/Operational" do servidor. Ligada ao
/// registro de <see cref="SessionHistory"/> da sessão em que aconteceu.
/// </summary>
public class SessionEvent
{
    public long Id { get; set; }

    public string ServerName { get; set; } = string.Empty;

    /// <summary>EventRecordID no log do Windows (negativo = evento do modo de simulação).</summary>
    public long RecordId { get; set; }

    public SessionEventType Tipo { get; set; }

    /// <summary>Momento do evento (UTC).</summary>
    public DateTime DataHora { get; set; }

    public int RdsSessionId { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>Endereço de origem informado pelo Windows (IP ou "LOCAL").</summary>
    public string? ClientIp { get; set; }

    /// <summary>Código de motivo do evento 40 (só desconexões; null = não registrado).</summary>
    public int? MotivoCodigo { get; set; }

    public int? SessionHistoryId { get; set; }
    public SessionHistory? SessionHistory { get; set; }

    public string Motivo => DescreverMotivo(Tipo, MotivoCodigo);

    /// <summary>Texto do motivo a partir do código do evento 40.</summary>
    public static string DescreverMotivo(SessionEventType tipo, int? codigo) => tipo switch
    {
        SessionEventType.Reconexao => "Reconectou",
        _ => codigo switch
        {
            null => "Desconectou (motivo não registrado)",
            0    => "Conexão perdida (rede ou cliente fechado)",
            1    => "Desconectada por ferramenta administrativa",
            2    => "Logoff por ferramenta administrativa",
            3    => "Tempo ocioso excedido",
            4    => "Tempo de logon excedido",
            5    => "Substituída por outra conexão",
            6    => "Servidor sem memória",
            7    => "Servidor recusou a conexão",
            9    => "Permissão insuficiente",
            10   => "Credenciais novas exigidas",
            11   => "Usuário desconectou",
            12   => "Usuário fez logoff",
            _    => $"Desconectou (código {codigo})",
        },
    };
}
