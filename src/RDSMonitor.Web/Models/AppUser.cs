namespace RDSMonitor.Web.Models;

/// <summary>Como o usuário entra no portal.</summary>
public enum TipoAutenticacao
{
    /// <summary>Usuário e senha do próprio portal.</summary>
    Local,
    /// <summary>Conta Microsoft 365 (Entra ID), identificada pelo e-mail.</summary>
    Microsoft365,
}

/// <summary>
/// Usuário com acesso ao painel RDSManager. Corresponde à tabela "Users"
/// prevista no escopo do projeto.
/// </summary>
public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Perfil antigo (enum). Mantido só para migrar bancos anteriores — use <see cref="PerfilId"/>.</summary>
    public UserProfile Profile { get; set; }

    /// <summary>Perfil de acesso (tabela Perfis) com as permissões do usuário.</summary>
    public int? PerfilId { get; set; }
    public RDSMonitor.Web.Data.Entities.Perfil? Perfil { get; set; }

    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? UltimoLogin { get; set; }

    // ── Proteção contra força bruta ──────────────────────────
    /// <summary>Falhas de senha consecutivas desde o último login bem-sucedido.</summary>
    public int AccessFailedCount { get; set; }

    /// <summary>Enquanto estiver no futuro (UTC), o login é recusado mesmo com a senha certa.</summary>
    public DateTime? LockoutEnd { get; set; }

    /// <summary>
    /// Obriga o usuário a definir uma senha própria antes de entrar no portal.
    /// Marcado para usuários novos e quando o administrador redefine a senha.
    /// </summary>
    public bool TrocarSenhaNoProximoLogin { get; set; }

    /// <summary>E-mail: recuperação de senha e, nas contas Microsoft 365, o login.</summary>
    public string? Email { get; set; }

    public TipoAutenticacao TipoAutenticacao { get; set; } = TipoAutenticacao.Local;

    /// <summary>A senha vence depois da validade definida em Configurações (contas locais).</summary>
    public bool SenhaExpira { get; set; }

    /// <summary>Última troca de senha (UTC); null = desde a criação da conta.</summary>
    public DateTime? SenhaAlteradaEm { get; set; }

    /// <summary>Senha temporária enviada por e-mail ("Esqueci minha senha") e sua validade (UTC).</summary>
    public string? SenhaTemporariaHash { get; set; }
    public DateTime? SenhaTemporariaExpiraEm { get; set; }

    /// <summary>Data em que a senha vence (UTC), ou null se não expira.</summary>
    public DateTime? SenhaVenceEm(int validadeDias) =>
        SenhaExpira && TipoAutenticacao == TipoAutenticacao.Local
            ? (SenhaAlteradaEm ?? CriadoEm).AddDays(validadeDias)
            : null;
}
