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
    public DbSet<SessionEvent>   SessionEvents   => Set<SessionEvent>();
    public DbSet<AgentePacote>   AgentePacotes   => Set<AgentePacote>();
    public DbSet<Configuracao>   Configuracoes   => Set<Configuracao>();
    public DbSet<Command>        Commands        => Set<Command>();
    public DbSet<ServerProcess>  Processes       => Set<ServerProcess>();

    // ── Perfis e auditoria ───────────────────────────────────
    public DbSet<Perfil>         Perfis          => Set<Perfil>();
    public DbSet<AuditoriaAcao>  Auditoria       => Set<AuditoriaAcao>();
    public DbSet<AcessoLog>      AcessoLogs      => Set<AcessoLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── AppUser ──────────────────────────────────────────
        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Username).IsRequired().HasMaxLength(100);
            e.Property(u => u.DisplayName).IsRequired().HasMaxLength(150);
            e.Property(u => u.Email).HasMaxLength(200);
            e.HasIndex(u => u.Email);
            e.HasOne(u => u.Perfil)
             .WithMany(p => p.Usuarios)
             .HasForeignKey(u => u.PerfilId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Perfil ───────────────────────────────────────────
        modelBuilder.Entity<Perfil>(e =>
        {
            e.ToTable("Perfis");
            e.HasIndex(p => p.Nome).IsUnique();
            e.Property(p => p.Nome).IsRequired().HasMaxLength(60);
            e.Property(p => p.Descricao).HasMaxLength(250);
            e.Property(p => p.Permissoes).IsRequired().HasDefaultValue("");
            e.Ignore(p => p.AcessoTotal);
            e.Ignore(p => p.ListaPermissoes);
        });

        // ── AuditoriaAcao ────────────────────────────────────
        modelBuilder.Entity<AuditoriaAcao>(e =>
        {
            e.ToTable("Auditoria");
            e.HasIndex(a => a.DataHora);
            e.HasIndex(a => a.CommandId);
            e.Property(a => a.OperadorLogin).IsRequired().HasMaxLength(100);
            e.Property(a => a.OperadorNome).HasMaxLength(150);
            e.Property(a => a.OperadorPerfil).HasMaxLength(60);
            e.Property(a => a.ServidorNome).IsRequired().HasMaxLength(100);
            e.Property(a => a.UsuarioAlvo).HasMaxLength(150);
            e.Property(a => a.Processo).HasMaxLength(260);
            e.Property(a => a.Detalhes).HasMaxLength(1000);
            e.Property(a => a.Resultado).HasMaxLength(2000);
        });

        // ── AcessoLog ────────────────────────────────────────
        modelBuilder.Entity<AcessoLog>(e =>
        {
            e.ToTable("AcessoLog");
            e.HasIndex(a => a.DataHora);
            e.Property(a => a.Usuario).IsRequired().HasMaxLength(100);
            e.Property(a => a.Ip).HasMaxLength(64);
            e.Property(a => a.Detalhe).HasMaxLength(250);
        });

        // ── Server ───────────────────────────────────────────
        modelBuilder.Entity<Server>(e =>
        {
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.Name).IsRequired().HasMaxLength(100);
            e.Property(s => s.Cliente).HasMaxLength(100);
            e.Property(s => s.AgentVersion).HasMaxLength(30);
            e.Property(s => s.VersaoAlvo).HasMaxLength(30);
            e.Property(s => s.AtualizacaoStatus).HasMaxLength(40);
            e.Property(s => s.AtualizacaoMensagem).HasMaxLength(500);
        });

        // ── Configuracao ─────────────────────────────────────
        modelBuilder.Entity<Configuracao>(e =>
        {
            e.ToTable("Configuracoes");
            e.HasKey(c => c.Chave);
            e.Property(c => c.Chave).HasMaxLength(80);
            e.Property(c => c.AlteradoPor).HasMaxLength(100);
        });

        // ── AgentePacote ─────────────────────────────────────
        modelBuilder.Entity<AgentePacote>(e =>
        {
            e.ToTable("AgentePacotes");
            e.HasIndex(p => p.Versao).IsUnique();
            e.Property(p => p.Versao).IsRequired().HasMaxLength(30);
            e.Property(p => p.NomeArquivo).IsRequired().HasMaxLength(200);
            e.Property(p => p.NomeOriginal).HasMaxLength(260);
            e.Property(p => p.Sha256).IsRequired().HasMaxLength(64);
            e.Property(p => p.EnviadoPor).HasMaxLength(100);
        });

        // ── Session ──────────────────────────────────────────
        modelBuilder.Entity<Session>(e =>
        {
            // Um servidor não pode ter dois registros com o mesmo ID de sessão RDS
            e.HasIndex(s => new { s.ServerId, s.RdsSessionId }).IsUnique();
            e.Property(s => s.ClientName).HasMaxLength(100);
            e.Property(s => s.ClientIp).HasMaxLength(64);
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
            e.Property(h => h.Cliente).HasMaxLength(100);
            e.Property(h => h.LogoffReason).HasMaxLength(60);
            e.Property(h => h.ClientName).HasMaxLength(100);
            e.Property(h => h.ClientIp).HasMaxLength(64);
            e.HasIndex(h => h.LogonTime);
        });

        // ── SessionEvent ─────────────────────────────────────
        modelBuilder.Entity<SessionEvent>(e =>
        {
            e.ToTable("SessionEvents");
            // O agente reenvia eventos se o heartbeat falhar: a chave evita duplicados
            e.HasIndex(x => new { x.ServerName, x.RecordId, x.DataHora }).IsUnique();
            e.HasIndex(x => x.DataHora);
            e.Property(x => x.ServerName).IsRequired().HasMaxLength(100);
            e.Property(x => x.Username).IsRequired().HasMaxLength(150);
            e.Property(x => x.ClientIp).HasMaxLength(64);
            e.Property(x => x.Operador).HasMaxLength(100);
            e.Property(x => x.DesconectadoPorUsuario).HasMaxLength(150);
            e.Ignore(x => x.Motivo);
            e.Ignore(x => x.Nome);
            e.HasOne(x => x.SessionHistory)
             .WithMany(h => h.Eventos)
             .HasForeignKey(x => x.SessionHistoryId)
             .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
