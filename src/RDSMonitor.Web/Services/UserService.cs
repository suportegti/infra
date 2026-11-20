using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Models;
using RDSMonitor.Web.Security;

namespace RDSMonitor.Web.Services;

public class UserService : IUserService
{
    private readonly AppDbContext _db;
    private readonly PasswordHasher<AppUser> _hasher = new();
    private readonly IPermissaoService _permissoes;

    public UserService(AppDbContext db, IPermissaoService permissoes)
    {
        _db = db;
        _permissoes = permissoes;
    }

    /// <summary>
    /// Impede que o sistema fique sem nenhum administrador ativo (alguém
    /// precisa conseguir gerenciar usuários e perfis).
    /// </summary>
    private async Task<bool> EhUltimoAdministradorAtivoAsync(AppUser user)
    {
        var perfilAdmin = await _db.Perfis.Where(p => p.Sistema && p.Nome == Permissoes.PerfilAdministrador)
                                          .Select(p => (int?)p.Id).FirstOrDefaultAsync();
        if (perfilAdmin is null || user.PerfilId != perfilAdmin || !user.Ativo) return false;

        return !await _db.Users.AnyAsync(u => u.Id != user.Id && u.Ativo && u.PerfilId == perfilAdmin);
    }

    // TODO: quando a integração com Active Directory / Microsoft Entra ID
    // for implementada (conforme o escopo do projeto), este método deve
    // validar a senha contra o diretório em vez do hash local — ou usar o
    // hash local apenas como alternativa para contas de serviço.
    public async Task<AppUser?> ValidarCredenciaisAsync(string username, string password)
    {
        var user = await _db.Users.Include(u => u.Perfil).FirstOrDefaultAsync(u =>
            u.Username.ToLower() == username.ToLower() && u.Ativo);

        if (user is null)
        {
            return null;
        }

        var resultado = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);

        return resultado == PasswordVerificationResult.Success ? user : null;
    }

    public async Task RegistrarLoginAsync(int userId)
    {
        var user = await _db.Users.FindAsync(userId);

        if (user is not null)
        {
            user.UltimoLogin = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task<PasswordChangeResult> AlterarSenhaAsync(string username, string senhaAtual, string novaSenha, string? ip = null)
    {
        username = (username ?? string.Empty).Trim();
        var agora = DateTime.UtcNow;

        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.Username.ToLower() == username.ToLower() && u.Ativo);

        if (user is null)
        {
            return PasswordChangeResult.CredenciaisInvalidas;
        }

        if (user.TipoAutenticacao == TipoAutenticacao.Microsoft365)
        {
            return PasswordChangeResult.CredenciaisInvalidas;   // a senha é a da conta Microsoft
        }

        if (user.LockoutEnd is { } fim && fim > agora)
        {
            return PasswordChangeResult.Bloqueado;
        }

        var resultado = _hasher.VerifyHashedPassword(user, user.PasswordHash, senhaAtual ?? string.Empty);

        // "Senha atual" também pode ser a temporária enviada por e-mail, enquanto válida
        if (resultado == PasswordVerificationResult.Failed && user.SenhaTemporariaHash is not null
            && user.SenhaTemporariaExpiraEm > agora
            && _hasher.VerifyHashedPassword(user, user.SenhaTemporariaHash, senhaAtual ?? string.Empty) != PasswordVerificationResult.Failed)
        {
            resultado = PasswordVerificationResult.Success;
        }

        if (resultado == PasswordVerificationResult.Failed)
        {
            // Mesma regra do login: a tela de troca não pode virar atalho para força bruta
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= AutenticacaoService.MaxTentativas)
            {
                user.AccessFailedCount = 0;
                user.LockoutEnd        = agora + AutenticacaoService.JanelaBloqueio;
            }
            Registrar(user.Username, ip, RDSMonitor.Web.Data.Entities.AcessoResultado.SenhaIncorreta, "troca de senha");
            await _db.SaveChangesAsync();
            return PasswordChangeResult.CredenciaisInvalidas;
        }

        if (string.IsNullOrWhiteSpace(novaSenha) || novaSenha.Length < 8)
        {
            return PasswordChangeResult.NovaSenhaInvalida;
        }

        if (string.Equals(novaSenha, senhaAtual, StringComparison.Ordinal))
        {
            return PasswordChangeResult.SenhaIgualAtual;
        }

        user.PasswordHash              = _hasher.HashPassword(user, novaSenha);
        user.TrocarSenhaNoProximoLogin = false;
        user.AccessFailedCount         = 0;
        user.SenhaAlteradaEm           = agora;
        user.SenhaTemporariaHash       = null;
        user.SenhaTemporariaExpiraEm   = null;
        Registrar(user.Username, ip, RDSMonitor.Web.Data.Entities.AcessoResultado.SenhaAlterada);
        await _db.SaveChangesAsync();

        return PasswordChangeResult.Sucesso;
    }

    private void Registrar(string usuario, string? ip, RDSMonitor.Web.Data.Entities.AcessoResultado resultado, string? detalhe = null)
        => _db.AcessoLogs.Add(new RDSMonitor.Web.Data.Entities.AcessoLog
        {
            DataHora  = DateTime.UtcNow,
            Usuario   = usuario.Length > 100 ? usuario[..100] : usuario,
            Ip        = ip is { Length: > 64 } ? ip[..64] : ip,
            Resultado = resultado,
            Detalhe   = detalhe,
        });

    public async Task<(List<RDSMonitor.Web.Data.Entities.AcessoLog>, bool)> ListarAcessosAsync(
        string username, DateTime? deUtc, DateTime? ateUtc, int limite = 500)
    {
        var u = username.ToLower();
        var q = _db.AcessoLogs.AsNoTracking().Where(a => a.Usuario.ToLower() == u);
        if (deUtc is { } de)   q = q.Where(a => a.DataHora >= de);
        if (ateUtc is { } ate) q = q.Where(a => a.DataHora <= ate);

        limite = Math.Clamp(limite, 1, 10_000);
        var itens = await q.OrderByDescending(a => a.DataHora).Take(limite + 1).ToListAsync();
        var truncado = itens.Count > limite;
        if (truncado) itens.RemoveAt(itens.Count - 1);
        return (itens, truncado);
    }

    public async Task<List<AppUser>> ListarAsync()
    {
        return await _db.Users.AsNoTracking().Include(u => u.Perfil).OrderBy(u => u.Username).ToListAsync();
    }

    public async Task<AppUser?> ObterPorIdAsync(int id)
    {
        return await _db.Users.AsNoTracking().Include(u => u.Perfil).FirstOrDefaultAsync(u => u.Id == id);
    }

    private static readonly System.Text.RegularExpressions.Regex EmailValido =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Valida e-mail (único, obrigatório nas contas Microsoft 365).</summary>
    private async Task<UserOperationResult?> ValidarEmailAsync(DadosUsuario d, int? ignorarId)
    {
        var email = d.Email?.Trim();
        if (string.IsNullOrEmpty(email))
            return d.Tipo == TipoAutenticacao.Microsoft365 ? UserOperationResult.EmailObrigatorio : null;
        if (email.Length > 200 || !EmailValido.IsMatch(email))
            return d.Tipo == TipoAutenticacao.Microsoft365 ? UserOperationResult.EmailObrigatorio : UserOperationResult.DadosInvalidos;

        var e = email.ToLower();
        return await _db.Users.AnyAsync(u => u.Id != ignorarId && u.Email != null && u.Email.ToLower() == e)
            ? UserOperationResult.EmailJaExiste
            : null;
    }

    public async Task<UserOperationResult> CriarAsync(string username, string? senhaInicial, DadosUsuario d)
    {
        username = username?.Trim() ?? string.Empty;
        var displayName = d.DisplayName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(displayName))
            return UserOperationResult.DadosInvalidos;

        // Conta local precisa de senha inicial; a do Microsoft 365 não tem senha no portal
        if (d.Tipo == TipoAutenticacao.Local && (string.IsNullOrWhiteSpace(senhaInicial) || senhaInicial.Length < 8))
            return UserOperationResult.SenhaInvalida;

        if (!await _db.Perfis.AnyAsync(p => p.Id == d.PerfilId))
            return UserOperationResult.DadosInvalidos;

        if (await _db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower()))
            return UserOperationResult.UsuarioJaExiste;

        if (await ValidarEmailAsync(d, ignorarId: null) is { } erroEmail)
            return erroEmail;

        var novoUsuario = new AppUser
        {
            Username         = username,
            DisplayName      = displayName,
            PerfilId         = d.PerfilId,
            Ativo            = true,
            Email            = string.IsNullOrWhiteSpace(d.Email) ? null : d.Email.Trim(),
            TipoAutenticacao = d.Tipo,
            SenhaExpira      = d.Tipo == TipoAutenticacao.Local && d.SenhaExpira,
            TrocarSenhaNoProximoLogin = d.Tipo == TipoAutenticacao.Local && d.ExigirTroca,
            SenhaAlteradaEm  = DateTime.UtcNow,
        };

        // Microsoft 365: senha local aleatória e descartada (nunca usada)
        novoUsuario.PasswordHash = _hasher.HashPassword(novoUsuario,
            d.Tipo == TipoAutenticacao.Local ? senhaInicial! : Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

        _db.Users.Add(novoUsuario);
        await _db.SaveChangesAsync();

        return UserOperationResult.Sucesso;
    }

    public async Task<UserOperationResult> EditarAsync(int id, string? novaSenha, DadosUsuario d)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return UserOperationResult.NaoEncontrado;

        var displayName = d.DisplayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(displayName)) return UserOperationResult.DadosInvalidos;

        if (d.Tipo == TipoAutenticacao.Local && !string.IsNullOrWhiteSpace(novaSenha) && novaSenha.Length < 8)
            return UserOperationResult.SenhaInvalida;

        if (!await _db.Perfis.AnyAsync(p => p.Id == d.PerfilId)) return UserOperationResult.DadosInvalidos;

        if (user.PerfilId != d.PerfilId && await EhUltimoAdministradorAtivoAsync(user))
            return UserOperationResult.UltimoAdministrador;

        if (await ValidarEmailAsync(d, ignorarId: id) is { } erroEmail)
            return erroEmail;

        user.DisplayName      = displayName;
        user.PerfilId         = d.PerfilId;
        user.Email            = string.IsNullOrWhiteSpace(d.Email) ? null : d.Email.Trim();
        user.TipoAutenticacao = d.Tipo;
        user.SenhaExpira      = d.Tipo == TipoAutenticacao.Local && d.SenhaExpira;

        if (d.Tipo == TipoAutenticacao.Local && !string.IsNullOrWhiteSpace(novaSenha))
        {
            user.PasswordHash    = _hasher.HashPassword(user, novaSenha);
            user.SenhaAlteradaEm = DateTime.UtcNow;
        }

        user.TrocarSenhaNoProximoLogin = d.Tipo == TipoAutenticacao.Local && d.ExigirTroca;

        await _db.SaveChangesAsync();
        _permissoes.Invalidar();

        return UserOperationResult.Sucesso;
    }

    public async Task<UserOperationResult> AlternarAtivoAsync(int id)
    {
        var user = await _db.Users.FindAsync(id);

        if (user is null)
        {
            return UserOperationResult.NaoEncontrado;
        }

        if (user.Ativo && await EhUltimoAdministradorAtivoAsync(user))
        {
            return UserOperationResult.UltimoAdministrador;
        }

        user.Ativo = !user.Ativo;
        await _db.SaveChangesAsync();
        _permissoes.Invalidar();

        return UserOperationResult.Sucesso;
    }

    public async Task<UserOperationResult> DesbloquearAsync(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return UserOperationResult.NaoEncontrado;

        user.AccessFailedCount = 0;
        user.LockoutEnd        = null;
        await _db.SaveChangesAsync();
        return UserOperationResult.Sucesso;
    }

    public async Task<bool> DesbloquearAsync(string username)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
        if (user is null) return false;

        user.AccessFailedCount = 0;
        user.LockoutEnd        = null;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RedefinirSenhaAsync(string username, string novaSenha, bool exigirTroca = false)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());

        if (user is null)
        {
            return false;
        }

        user.PasswordHash = _hasher.HashPassword(user, novaSenha);
        user.SenhaAlteradaEm = DateTime.UtcNow;
        user.Ativo = true;
        if (user.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
            user.TipoAutenticacao = TipoAutenticacao.Local;   // recuperação de acesso sempre local
        user.TrocarSenhaNoProximoLogin = exigirTroca;

        // Recuperação de acesso: zera o bloqueio por tentativas, se houver
        user.AccessFailedCount = 0;
        user.LockoutEnd        = null;

        // Recuperação de acesso: o admin volta a ter o perfil Administrador
        if (user.Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            user.PerfilId = await _db.Perfis.Where(p => p.Sistema && p.Nome == Permissoes.PerfilAdministrador)
                                            .Select(p => (int?)p.Id).FirstOrDefaultAsync() ?? user.PerfilId;
        }

        await _db.SaveChangesAsync();
        _permissoes.Invalidar();

        return true;
    }
}
