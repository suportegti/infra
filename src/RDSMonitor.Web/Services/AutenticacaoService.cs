using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Services;

public enum AuthStatus { Sucesso, CredenciaisInvalidas, Bloqueado, TrocaDeSenhaExigida, SenhaExpirada, UsarMicrosoft365 }

/// <summary>Resultado de uma tentativa de login.</summary>
/// <param name="Usuario">Preenchido só em caso de sucesso.</param>
/// <param name="BloqueioMinutos">Minutos restantes de bloqueio, quando <see cref="AuthStatus.Bloqueado"/>.</param>
public sealed record AuthResultado(AppUser? Usuario, AuthStatus Status, int BloqueioMinutos = 0);

public interface IAutenticacaoService
{
    /// <summary>
    /// Valida as credenciais aplicando bloqueio por tentativas e registra a
    /// tentativa (sucesso ou falha) em <c>AcessoLog</c>.
    /// </summary>
    Task<AuthResultado> AutenticarAsync(string username, string password, string? ip);

    /// <summary>Registra uma tentativa recusada pelo limite de requisições por IP.</summary>
    Task RegistrarLimiteDeTaxaAsync(string? username, string? ip);
    /// <summary>Registra a saída do usuário (botão Sair) no histórico de acessos.</summary>
    Task RegistrarLogoutAsync(string username, string? ip);

    /// <summary>Registra um evento de acesso (login Microsoft 365, recuperação de senha...).</summary>
    Task RegistrarEventoAsync(string usuario, string? ip, AcessoResultado resultado, string? detalhe);
}

public class AutenticacaoService : IAutenticacaoService
{
    /// <summary>Falhas consecutivas que disparam o bloqueio temporário da conta.</summary>
    public const int MaxTentativas = 5;

    /// <summary>Duração do bloqueio após atingir <see cref="MaxTentativas"/>.</summary>
    public static readonly TimeSpan JanelaBloqueio = TimeSpan.FromMinutes(15);

    // Hash fixo de uma senha qualquer: verificado quando o usuário não existe,
    // para o tempo de resposta não denunciar quais logins são válidos.
    private static readonly string HashFalso = new PasswordHasher<AppUser>().HashPassword(new AppUser(), "__inexistente__");

    private readonly AppDbContext _db;
    private readonly PasswordHasher<AppUser> _hasher = new();

    private readonly IConfiguracaoService _config;

    public AutenticacaoService(AppDbContext db, IConfiguracaoService config)
    {
        _db = db;
        _config = config;
    }

    public async Task<AuthResultado> AutenticarAsync(string username, string password, string? ip)
    {
        username = (username ?? string.Empty).Trim();
        var agora = DateTime.UtcNow;

        var user = await _db.Users.Include(u => u.Perfil)
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());

        if (user is null)
        {
            _hasher.VerifyHashedPassword(new AppUser(), HashFalso, password ?? string.Empty);
            await RegistrarAsync(username, ip, AcessoResultado.UsuarioDesconhecido);
            return new(null, AuthStatus.CredenciaisInvalidas);
        }

        if (!user.Ativo)
        {
            await RegistrarAsync(username, ip, AcessoResultado.UsuarioDesabilitado);
            return new(null, AuthStatus.CredenciaisInvalidas);
        }

        if (user.LockoutEnd is { } fim && fim > agora)
        {
            var restam = (int)Math.Ceiling((fim - agora).TotalMinutes);
            await RegistrarAsync(username, ip, AcessoResultado.Bloqueado, $"{restam} min restantes");
            return new(null, AuthStatus.Bloqueado, restam);
        }

        if (user.TipoAutenticacao == TipoAutenticacao.Microsoft365)
        {
            // Conta Microsoft 365 não tem senha local: o login é pelo botão da Microsoft
            await RegistrarAsync(username, ip, AcessoResultado.ContaMicrosoft365);
            return new(null, AuthStatus.UsarMicrosoft365);
        }

        var ok = _hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty)
                 != PasswordVerificationResult.Failed;

        // Senha temporária enviada por e-mail (vale até expirar; obriga a criar uma senha nova)
        var temporaria = !ok && user.SenhaTemporariaHash is not null && user.SenhaTemporariaExpiraEm > agora
                         && _hasher.VerifyHashedPassword(user, user.SenhaTemporariaHash, password ?? string.Empty)
                            != PasswordVerificationResult.Failed;

        if (temporaria || (ok && user.TrocarSenhaNoProximoLogin))
        {
            // Senha certa, mas o usuário precisa definir a dele antes de entrar
            user.AccessFailedCount = 0;
            user.LockoutEnd        = null;
            await RegistrarAsync(username, ip, AcessoResultado.TrocaDeSenhaExigida,
                temporaria ? "entrou com a senha temporária" : null, salvar: false);
            await _db.SaveChangesAsync();
            return new(user, AuthStatus.TrocaDeSenhaExigida);
        }

        if (ok && user.SenhaVenceEm((await _config.PoliticaSenhaAsync()).ValidadeDias) is { } vence && vence <= agora)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd        = null;
            await RegistrarAsync(username, ip, AcessoResultado.SenhaExpirada, $"venceu em {Horario.Fmt(vence, "dd/MM/yyyy")}", salvar: false);
            await _db.SaveChangesAsync();
            return new(user, AuthStatus.SenhaExpirada);
        }

        if (ok)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd       = null;
            user.UltimoLogin      = agora;
            await RegistrarAsync(username, ip, AcessoResultado.Sucesso, salvar: false);
            await _db.SaveChangesAsync();
            return new(user, AuthStatus.Sucesso);
        }

        user.AccessFailedCount++;
        int bloqueioMin = 0;
        AcessoResultado resultado;
        string detalhe;

        if (user.AccessFailedCount >= MaxTentativas)
        {
            user.LockoutEnd       = agora + JanelaBloqueio;
            user.AccessFailedCount = 0;
            bloqueioMin           = (int)JanelaBloqueio.TotalMinutes;
            resultado             = AcessoResultado.Bloqueado;
            detalhe               = $"bloqueado por {bloqueioMin} min apos {MaxTentativas} tentativas";
        }
        else
        {
            resultado = AcessoResultado.SenhaIncorreta;
            detalhe   = $"tentativa {user.AccessFailedCount}/{MaxTentativas}";
        }

        await RegistrarAsync(username, ip, resultado, detalhe, salvar: false);
        await _db.SaveChangesAsync();

        return bloqueioMin > 0
            ? new(null, AuthStatus.Bloqueado, bloqueioMin)
            : new(null, AuthStatus.CredenciaisInvalidas);
    }

    public Task RegistrarLimiteDeTaxaAsync(string? username, string? ip) =>
        RegistrarAsync(username ?? string.Empty, ip, AcessoResultado.LimiteDeTaxa);

    public Task RegistrarLogoutAsync(string username, string? ip) =>
        RegistrarAsync(username, ip, AcessoResultado.Logout);

    public Task RegistrarEventoAsync(string usuario, string? ip, AcessoResultado resultado, string? detalhe) =>
        RegistrarAsync(usuario, ip, resultado, detalhe);

    private async Task RegistrarAsync(string usuario, string? ip, AcessoResultado resultado, string? detalhe = null, bool salvar = true)
    {
        _db.AcessoLogs.Add(new AcessoLog
        {
            DataHora  = DateTime.UtcNow,
            Usuario   = Limitar(usuario, 100) ?? string.Empty,
            Ip        = Limitar(ip, 64),
            Resultado = resultado,
            Detalhe   = Limitar(detalhe, 250),
        });
        if (salvar) await _db.SaveChangesAsync();
    }

    private static string? Limitar(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length > max ? s[..max] : s);
}
