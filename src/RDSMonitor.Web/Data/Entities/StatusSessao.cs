namespace RDSMonitor.Web.Data.Entities;

/// <summary>
/// Campo "Status" do histórico de sessões (coluna SessionHistory.LogoffReason).
/// Acompanha o ciclo de eventos do Windows:
///   21 Logon → Conectado · 24 Desconexão → Queda / Desconectado… ·
///   25 Reconexão → Reconectado · 23 Logoff → Logoff.
/// </summary>
public static class StatusSessao
{
    public const string Conectado           = "Conectado";
    public const string Queda               = "Queda de conexão";
    public const string Reconectado         = "Reconectado";
    public const string PeloUsuario         = "Desconectado pelo usuário";
    public const string PeloAdministrador   = "Desconectado pelo administrador";
    public const string FerramentaAdmin     = "Desconectado por ferramenta administrativa";
    public const string Ociosidade          = "Desconectado por ociosidade";
    public const string OutraConexao        = "Substituída por outra conexão";
    public const string Desconectado        = "Desconectado";
    public const string Logoff              = "Logoff";
    public const string LogoffAdministrador = "Logoff pelo administrador";

    /// <summary>
    /// Status depois de um evento e, quando foi outra pessoa, quem foi. Null =
    /// evento que não muda o status (autenticação, shell, logoff — o logoff é
    /// tratado ao fechar a sessão).
    /// </summary>
    public static (string Texto, string? Quem)? DeEvento(SessionEvent e) => e.Tipo switch
    {
        SessionEventType.Logon     => (Conectado, null),
        SessionEventType.Reconexao => (Reconectado, null),
        SessionEventType.Desconexao when e.Operador is not null => (PeloAdministrador, e.Operador),
        SessionEventType.Desconexao when e.DesconectadoPorSessao is { } por && por == e.RdsSessionId => (PeloUsuario, null),
        SessionEventType.Desconexao when e.DesconectadoPorSessao is > 0 =>
            (PeloAdministrador, e.DesconectadoPorUsuario ?? $"sessão {e.DesconectadoPorSessao}"),
        SessionEventType.Desconexao when e.DesconectadoPorSessao == 0 => (FerramentaAdmin, null),
        SessionEventType.Desconexao => (e.MotivoCodigo switch
        {
            0  => Queda,
            11 => PeloUsuario,
            1  => FerramentaAdmin,
            3  => Ociosidade,
            5  => OutraConexao,
            _  => Desconectado,
        }, null),
        _ => null,
    };

    /// <summary>Texto para a tela ("Agente"/"Admin" eram gravados pelas versões até a 1.5.0).</summary>
    public static string Exibir(string? valor) => valor switch
    {
        null or ""          => Conectado,
        "Agente" or "Admin" => Logoff,
        _                   => valor,
    };

    /// <summary>Cor do indicador (status-dot) na tela.</summary>
    public static string Css(string? valor) => Exibir(valor) switch
    {
        Conectado or Reconectado => "active",
        PeloUsuario or PeloAdministrador or Ociosidade => "idle",
        Logoff or LogoffAdministrador => "",
        _ => "disconnected",
    };
}
