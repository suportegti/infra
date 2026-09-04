using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<AppUser> Users => Set<AppUser>();

    // Próximas entidades a implementar, conforme o escopo do projeto:
    // Servers, Sessions, Processes, SessionHistory, Alerts, Commands, Audit.

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasIndex(u => u.Username).IsUnique();
            entity.Property(u => u.Username).IsRequired().HasMaxLength(100);
            entity.Property(u => u.DisplayName).IsRequired().HasMaxLength(150);
        });
    }
}
