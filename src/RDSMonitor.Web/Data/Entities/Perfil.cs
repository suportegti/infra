using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Data.Entities;

/// <summary>
/// Perfil de acesso ao portal com a lista de funcionalidades liberadas
/// (códigos de <see cref="Security.Permissoes"/>). Os perfis do sistema
/// (Administrador, Operador e Auditor) não podem ser excluídos nem renomeados;
/// o Administrador tem sempre todas as permissões, para que ninguém perca o
/// acesso à configuração.
/// </summary>
public class Perfil
{
    public int Id { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string? Descricao { get; set; }

    /// <summary>Perfil padrão do sistema (não pode ser excluído nem renomeado).</summary>
    public bool Sistema { get; set; }

    /// <summary>Códigos das permissões separados por vírgula.</summary>
    public string Permissoes { get; set; } = string.Empty;

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? AlteradoEm { get; set; }
    public string? AlteradoPor { get; set; }

    public ICollection<AppUser> Usuarios { get; set; } = new List<AppUser>();

    /// <summary>Administrador do sistema: todas as permissões, sempre.</summary>
    public bool AcessoTotal => Sistema && Nome == Security.Permissoes.PerfilAdministrador;

    public IReadOnlySet<string> ListaPermissoes =>
        AcessoTotal
            ? Security.Permissoes.Todas.ToHashSet()
            : Permissoes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(Security.Permissoes.Existe)
                        .ToHashSet();

    public void DefinirPermissoes(IEnumerable<string> codigos) =>
        Permissoes = string.Join(",", codigos.Where(Security.Permissoes.Existe).Distinct().OrderBy(c => c));
}
