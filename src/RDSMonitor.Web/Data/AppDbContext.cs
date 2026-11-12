using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // ── Gestão de usuários do portal ─────────────────────────
    public DbSet<AppUser> Users => Set<AppUser>();

    // ── Dados coletados pelos agentes ────────────────────────
    public DbSet<Server>         Servers         => Set<Server>();
    public DbSet<Session>        Sessions        => Set<Session>();
    public DbSet<SessionHistory> SessionHistories => Set<SessionHistory>();
    public DbSet<Command>        Commands        => Set<Command>();
    public DbSet<ServerProcess>  Processes       => Set<ServerProcess>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── AppUser ──────────────────────────────────────────
        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Username).IsRequired().HasMaxLength(100);
            e.Property(u => u.DisplayName).IsRequired().HasMaxLength(150);
        });

        // ── Server ───────────────────────────────────────────
        modelBuilder.Entity<Server>(e =>
        {
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.Name).IsRequired().HasMaxLength(100);
        });

        // ── Session ──────────────────────────────────────────
        modelBuilder.Entity<Session>(e =>
        {
            // Um servidor não pode ter dois registros com o mesmo ID de sessão RDS
            e.HasIndex(s => new { s.ServerId, s.RdsSessionId }).IsUnique();
            e.HasOne(s => s.Server)
             .WithMany(sv => sv.Sessions)
             .HasForeignKey(s => s.ServerId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Command ──────────────────────────────────────────
        modelBuilder.Entity<Command>(e =>
        {
            e.HasOne(c => c.Server)
             .WithMany(sv => sv.Commands)
             .HasForeignKey(c => c.ServerId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Property(c => c.ParametersJson).HasDefaultValue("{}");
        });

        // ── ServerProcess ────────────────────────────────────
        modelBuilder.Entity<ServerProcess>(e =>
        {
            e.ToTable("Processes");
            e.HasIndex(p => new { p.ServerId, p.RdsSessionId });
            e.Property(p => p.Name).IsRequired().HasMaxLength(260);
            e.Property(p => p.Username).HasMaxLength(150);
            e.HasOne(p => p.Server)
             .WithMany(sv => sv.Processes)
             .HasForeignKey(p => p.ServerId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── SessionHistory ───────────────────────────────────
        modelBuilder.Entity<SessionHistory>(e =>
        {
            e.Property(h => h.ServerName).IsRequired().HasMaxLength(100);
            e.Property(h => h.Username).IsRequired().HasMaxLength(150);
        });
    }
}
