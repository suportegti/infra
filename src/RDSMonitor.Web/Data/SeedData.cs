using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Data;

public static class SeedData
{
    /// <summary>
    /// Cria um usuário de demonstração para cada perfil de acesso na
    /// primeira execução, para permitir testar a tela de login.
    /// </summary>
    public static async Task EnsureSeededAsync(AppDbContext db)
    {
        if (await db.Users.AnyAsync())
        {
            return;
        }

        var hasher = new PasswordHasher<AppUser>();

        var usuarios = new List<AppUser>
        {
            new() { Username = "admin", DisplayName = "Administrador do Sistema", Profile = UserProfile.Administrador },
            new() { Username = "operador", DisplayName = "Operador Padrão", Profile = UserProfile.Operador },
            new() { Username = "auditor", DisplayName = "Auditor Padrão", Profile = UserProfile.Auditor },
        };

        foreach (var usuario in usuarios)
        {
            // Senha inicial de demonstração — trocar/remover antes de usar em produção.
            usuario.PasswordHash = hasher.HashPassword(usuario, "Rds@1234");
        }

        db.Users.AddRange(usuarios);
        await db.SaveChangesAsync();
    }
}
