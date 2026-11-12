using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Data;

public static class SeedData
{
    /// <summary>
    /// Na primeira execução: cria os usuários de demonstração (um por perfil)
    /// e pré-cadastra os 5 servidores RDS para que o dashboard já os liste
    /// antes que os agentes sejam instalados.
    /// </summary>
    public static async Task EnsureSeededAsync(AppDbContext db)
    {
        await SeedUsersAsync(db);
        await SeedServersAsync(db);
    }

    private static async Task SeedUsersAsync(AppDbContext db)
    {
        if (await db.Users.AnyAsync()) return;

        var hasher = new PasswordHasher<AppUser>();
        var usuarios = new List<AppUser>
        {
            new() { Username = "admin",    DisplayName = "Administrador do Sistema", Profile = UserProfile.Administrador },
            new() { Username = "operador", DisplayName = "Operador Padrão",          Profile = UserProfile.Operador },
            new() { Username = "auditor",  DisplayName = "Auditor Padrão",           Profile = UserProfile.Auditor },
        };

        foreach (var u in usuarios)
            u.PasswordHash = hasher.HashPassword(u, "Rds@1234");

        db.Users.AddRange(usuarios);
        await db.SaveChangesAsync();
    }

    private static async Task SeedServersAsync(AppDbContext db)
    {
        if (await db.Servers.AnyAsync()) return;

        // Pré-cadastra os 5 servidores do ambiente. Eles aparecerão como
        // "offline" até o agente ser instalado e enviar o primeiro heartbeat.
        var servidores = new List<Server>
        {
            new() { Name = "RDS01", DisplayName = "RDS Produção 1" },
            new() { Name = "RDS02", DisplayName = "RDS Produção 2" },
            new() { Name = "RDS03", DisplayName = "RDS Produção 3" },
            new() { Name = "RDS04", DisplayName = "RDS Produção 4" },
            new() { Name = "RDS05", DisplayName = "RDS Produção 5" },
        };

        db.Servers.AddRange(servidores);
        await db.SaveChangesAsync();
    }
}
