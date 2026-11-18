namespace RDSMonitor.Web.Data.Entities;

/// <summary>
/// Registro de auditoria de uma ação executada por um operador do portal
/// (logoff, desconectar, derrubar processo, mensagem, reinícios). É gravado
/// quando o operador confirma a ação e atualizado com o resultado devolvido
/// pelo agente. Não tem chave estrangeira para servidores/comandos: o
/// histórico continua íntegro mesmo que o servidor seja excluído.
/// </summary>
public class AuditoriaAcao
{
    public long Id { get; set; }

    /// <summary>Momento da solicitação (UTC).</summary>
    public DateTime DataHora { get; set; } = DateTime.UtcNow;

    // Quem fez
    public string OperadorLogin { get; set; } = string.Empty;
    public string? OperadorNome { get; set; }
    public string? OperadorPerfil { get; set; }

    // O que fez
    public CommandType Acao { get; set; }

    // Onde / em quem
    public int? ServidorId { get; set; }
    public string ServidorNome { get; set; } = string.Empty;
    public int? SessaoId { get; set; }
    public string? UsuarioAlvo { get; set; }
    public string? Processo { get; set; }
    public int? Pid { get; set; }
    public string? Detalhes { get; set; }

    // Resultado
    public int? CommandId { get; set; }
    public CommandStatus Status { get; set; } = CommandStatus.Pending;
    public string? Resultado { get; set; }
    public DateTime? ConcluidoEm { get; set; }
}
