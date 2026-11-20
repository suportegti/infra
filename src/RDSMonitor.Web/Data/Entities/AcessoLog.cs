namespace RDSMonitor.Web.Data.Entities;

/// <summary>Como terminou uma tentativa de autenticação no portal.</summary>
public enum AcessoResultado
{
    Sucesso,
    SenhaIncorreta,
    UsuarioDesconhecido,
    UsuarioDesabilitado,
    /// <summary>Conta bloqueada por excesso de tentativas no momento da tentativa.</summary>
    Bloqueado,
    /// <summary>Recusado pelo limite de requisições por IP (rate limit).</summary>
    LimiteDeTaxa,
    /// <summary>Usuário saiu do portal (botão Sair).</summary>
    Logout,
    /// <summary>Senha correta, mas a troca é obrigatória antes de entrar.</summary>
    TrocaDeSenhaExigida,
    /// <summary>Senha alterada pelo próprio usuário.</summary>
    SenhaAlterada,
    /// <summary>Senha temporária enviada por e-mail ("Esqueci minha senha").</summary>
    RecuperacaoSolicitada,
    /// <summary>Senha correta, mas vencida: a troca é obrigatória.</summary>
    SenhaExpirada,
    /// <summary>Conta Microsoft 365 tentando entrar com senha local.</summary>
    ContaMicrosoft365,
}

/// <summary>
/// Registro de cada tentativa de login no portal (sucesso ou falha), para dar
/// visibilidade a ataques de força bruta. Não tem chave estrangeira para Users:
/// o usuário digitado pode nem existir, e o histórico continua válido mesmo que
/// a conta seja removida no futuro.
/// </summary>
public class AcessoLog
{
    public long Id { get; set; }

    /// <summary>Momento da tentativa (UTC).</summary>
    public DateTime DataHora { get; set; } = DateTime.UtcNow;

    /// <summary>Login digitado (pode não existir).</summary>
    public string Usuario { get; set; } = string.Empty;

    /// <summary>Endereço de origem, quando conhecido.</summary>
    public string? Ip { get; set; }

    public AcessoResultado Resultado { get; set; }

    /// <summary>Texto curto de contexto (ex.: minutos de bloqueio).</summary>
    public string? Detalhe { get; set; }

    public static string Descrever(AcessoResultado r) => r switch
    {
        AcessoResultado.Sucesso             => "Entrou",
        AcessoResultado.SenhaIncorreta      => "Senha incorreta",
        AcessoResultado.UsuarioDesconhecido => "Usuário inexistente",
        AcessoResultado.UsuarioDesabilitado => "Usuário desabilitado",
        AcessoResultado.Bloqueado           => "Bloqueado",
        AcessoResultado.LimiteDeTaxa        => "Limite por IP",
        AcessoResultado.Logout              => "Saiu",
        AcessoResultado.TrocaDeSenhaExigida => "Troca de senha exigida",
        AcessoResultado.SenhaAlterada       => "Senha alterada",
        AcessoResultado.RecuperacaoSolicitada => "Senha temporária enviada",
        AcessoResultado.SenhaExpirada       => "Senha vencida",
        AcessoResultado.ContaMicrosoft365   => "Conta Microsoft 365 (use o botão da Microsoft)",
        _                                   => r.ToString(),
    };

    /// <summary>Classe do indicador colorido (status-dot) na tela.</summary>
    public static string Css(AcessoResultado r) => r switch
    {
        AcessoResultado.Sucesso or AcessoResultado.SenhaAlterada => "active",
        AcessoResultado.Logout or AcessoResultado.TrocaDeSenhaExigida or AcessoResultado.SenhaExpirada
            or AcessoResultado.RecuperacaoSolicitada or AcessoResultado.ContaMicrosoft365 => "idle",
        _ => "disconnected",
    };
}
