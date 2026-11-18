using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models;
using RDSMonitor.Web.Security;

namespace RDSMonitor.Web.Data;

public static class SeedData
{
    /// <summary>
    /// Na primeira execução cria os perfis padrão (Administrador, Operador,
    /// Auditor) e um único usuário, "admin" (senha Rds@1234). Operador e
    /// Auditor são só perfis: os usuários reais são criados em Usuários do
    /// sistema. Com <paramref name="demonstracao"/> (ambiente Development)
    /// cria também os 5 servidores RDS de exemplo.
    /// </summary>
    public static async Task EnsureSeededAsync(AppDbContext db, bool demonstracao)
    {
        await SeedPerfisAsync(db);
        await SeedUsersAsync(db);
        await MigrarPerfisDosUsuariosAsync(db);
        if (demonstracao) await SeedServersAsync(db);
    }

    /// <summary>
    /// Garante os 3 perfis do sistema com as permissões padrão. Perfis já
    /// existentes não são alterados (o administrador pode tê-los customizado).
    /// </summary>
    private static async Task SeedPerfisAsync(AppDbContext db)
    {
        var padrao = new (string Nome, string Descricao)[]
        {
            (Permissoes.PerfilAdministrador, "Acesso total ao sistema. Não pode ser alterado."),
            (Permissoes.PerfilOperador,      "Acompanha sessões, processos e servidores."),
            (Permissoes.PerfilAuditor,       "Consulta relatórios e o log de auditoria."),
        };

        foreach (var (nome, descricao) in padrao)
        {
            if (await db.Perfis.AnyAsync(p => p.Nome == nome)) continue;

            var perfil = new Perfil { Nome = nome, Descricao = descricao, Sistema = true };
            perfil.DefinirPermissoes(Permissoes.PadraoDoPerfil(nome));
            db.Perfis.Add(perfil);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Bancos anteriores guardavam o perfil como enum (coluna Profile): associa
    /// cada usuário sem PerfilId ao perfil do sistema de mesmo nome.
    /// </summary>
    private static async Task MigrarPerfisDosUsuariosAsync(AppDbContext db)
    {
        var semPerfil = await db.Users.Where(u => u.PerfilId == null).ToListAsync();
        if (semPerfil.Count == 0) return;

        var perfis = await db.Perfis.Where(p => p.Sistema).ToDictionaryAsync(p => p.Nome, p => p.Id);
        foreach (var u in semPerfil)
            u.PerfilId = perfis.TryGetValue(u.Profile.ToString(), out var id) ? id : perfis[Permissoes.PerfilOperador];

        await db.SaveChangesAsync();
    }

    /// <summary>Banco novo: cria só o "admin", já no perfil Administrador.</summary>
    private static async Task SeedUsersAsync(AppDbContext db)
    {
        if (await db.Users.AnyAsync()) return;

        var admin = new AppUser
        {
            Username    = "admin",
            DisplayName = "Administrador do Sistema",
            Profile     = UserProfile.Administrador,
            PerfilId    = await db.Perfis.Where(p => p.Nome == Permissoes.PerfilAdministrador).Select(p => (int?)p.Id).FirstAsync(),
        };
        admin.PasswordHash = new PasswordHasher<AppUser>().HashPassword(admin, "Rds@1234");

        db.Users.Add(admin);
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
            // Agente rodando na própria máquina de desenvolvimento (appsettings.Development.json do agente)
            new() { Name = "DEV-LOCAL", DisplayName = "Desenvolvimento (local)" },
        };

        db.Servers.AddRange(servidores);
        await db.SaveChangesAsync();
    }
}
