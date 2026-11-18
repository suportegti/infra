namespace RDSMonitor.Web.Models;

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
}
