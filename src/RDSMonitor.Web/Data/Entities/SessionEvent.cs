namespace RDSMonitor.Web.Data.Entities;

/// <summary>Evento do ciclo de vida da sessão (os valores numéricos ficam gravados no banco: só acrescentar no fim).</summary>
public enum SessionEventType
{
    /// <summary>Evento 24: sessão desconectada (continua aberta no servidor).</summary>
    Desconexao,
    /// <summary>Evento 25: o usuário voltou para uma sessão existente.</summary>
    Reconexao,
    /// <summary>Evento 21: logon da sessão concluído (início efetivo).</summary>
    Logon,
    /// <summary>Evento 23: logoff (sessão efetivamente encerrada).</summary>
    Logoff,
    /// <summary>Evento 22: shell/área de trabalho do usuário iniciado.</summary>
    Shell,
    /// <summary>Evento 1149 (RemoteConnectionManager): autenticação RDP aceita.</summary>
    Autenticacao,
}

/// <summary>
/// Evento do ciclo de vida de uma sessão RDS lido pelo agente dos logs
/// TerminalServices do servidor. Ligado ao registro de
/// <see cref="SessionHistory"/> da sessão em que aconteceu.
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

    /// <summary>ID da sessão RDS (0 na autenticação 1149, que acontece antes da sessão).</summary>
    public int RdsSessionId { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>Endereço de origem informado pelo Windows (IP ou "LOCAL").</summary>
    public string? ClientIp { get; set; }

    /// <summary>Código de motivo do evento 40 (só desconexões; null = não registrado).</summary>
    public int? MotivoCodigo { get; set; }

    /// <summary>Sessão que pediu a desconexão (evento 39): a própria = o usuário; outra = alguém nela; 0 = serviço.</summary>
    public int? DesconectadoPorSessao { get; set; }

    /// <summary>Usuário da sessão que pediu a desconexão, quando era outra sessão.</summary>
    public string? DesconectadoPorUsuario { get; set; }

    public int? SessionHistoryId { get; set; }
    public SessionHistory? SessionHistory { get; set; }

    /// <summary>Login do operador quando a desconexão foi pedida pelo portal (Disconnect na tela Sessões).</summary>
    public string? Operador { get; set; }

    /// <summary>Registro de auditoria do comando do portal que causou esta desconexão.</summary>
    public long? AuditoriaId { get; set; }

    /// <summary>Nome curto do evento para a linha do tempo.</summary>
    public string Nome => Tipo switch
    {
        SessionEventType.Autenticacao => "Autenticação",
        SessionEventType.Logon        => "Logon",
        SessionEventType.Shell        => "Área de trabalho",
        SessionEventType.Desconexao   => "Desconexão",
        SessionEventType.Reconexao    => "Reconexão",
        SessionEventType.Logoff       => "Logoff",
        _                             => Tipo.ToString(),
    };

    /// <summary>Detalhe do evento (na desconexão: quem pediu ou o motivo).</summary>
    public string Motivo => Tipo switch
    {
        SessionEventType.Autenticacao => "Autenticação RDP aceita (evento 1149)",
        SessionEventType.Logon        => "Logon da sessão concluído (evento 21)",
        SessionEventType.Shell        => "Área de trabalho do usuário carregada (evento 22)",
        SessionEventType.Logoff       => "Sessão encerrada (evento 23)",
        SessionEventType.Reconexao    => "Voltou para a sessão aberta (evento 25)",
        _ when Operador is not null   => $"Desconectada pelo administrador ({Operador}) pelo portal",
        _ when DesconectadoPorSessao is { } por && por == RdsSessionId => "O próprio usuário desconectou",
        _ when DesconectadoPorSessao is > 0 => $"Desconectada pela sessão {DesconectadoPorSessao}" +
                                               (DesconectadoPorUsuario is null ? "" : $" ({DesconectadoPorUsuario})"),
        _ when DesconectadoPorSessao == 0 => "Desconectada por um serviço ou ferramenta no servidor",
        _ => DescreverMotivo(MotivoCodigo),
    };

    /// <summary>Texto do motivo a partir do código do evento 40.</summary>
    public static string DescreverMotivo(int? codigo) => codigo switch
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
    };
}
