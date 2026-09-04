namespace RDSMonitor.Web.Models;

/// <summary>
/// Usuário com acesso ao painel RDS Monitor. Corresponde à tabela "Users"
/// prevista no escopo do projeto.
/// </summary>
public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserProfile Profile { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? UltimoLogin { get; set; }
}
