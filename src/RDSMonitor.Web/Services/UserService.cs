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

    public async Task<PasswordChangeResult> AlterarSenhaAsync(string username, string senhaAtual, string novaSenha)
    {
        if (string.IsNullOrWhiteSpace(novaSenha) || novaSenha.Length < 8)
        {
            return PasswordChangeResult.NovaSenhaInvalida;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.Username.ToLower() == username.ToLower() && u.Ativo);

        if (user is null)
        {
            return PasswordChangeResult.CredenciaisInvalidas;
        }

        var resultado = _hasher.VerifyHashedPassword(user, user.PasswordHash, senhaAtual);

        if (resultado == PasswordVerificationResult.Failed)
        {
            return PasswordChangeResult.CredenciaisInvalidas;
        }

        user.PasswordHash = _hasher.HashPassword(user, novaSenha);
        await _db.SaveChangesAsync();

        return PasswordChangeResult.Sucesso;
    }

    public async Task<List<AppUser>> ListarAsync()
    {
        return await _db.Users.AsNoTracking().Include(u => u.Perfil).OrderBy(u => u.Username).ToListAsync();
    }

    public async Task<AppUser?> ObterPorIdAsync(int id)
    {
        return await _db.Users.AsNoTracking().Include(u => u.Perfil).FirstOrDefaultAsync(u => u.Id == id);
    }

    public async Task<UserOperationResult> CriarAsync(string username, string displayName, int perfilId, string senhaInicial)
    {
        username = username?.Trim() ?? string.Empty;
        displayName = displayName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(displayName))
        {
            return UserOperationResult.DadosInvalidos;
        }

        if (string.IsNullOrWhiteSpace(senhaInicial) || senhaInicial.Length < 8)
        {
            return UserOperationResult.SenhaInvalida;
        }

        if (!await _db.Perfis.AnyAsync(p => p.Id == perfilId))
        {
            return UserOperationResult.DadosInvalidos;
        }

        var jaExiste = await _db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower());

        if (jaExiste)
        {
            return UserOperationResult.UsuarioJaExiste;
        }

        var novoUsuario = new AppUser
        {
            Username = username,
            DisplayName = displayName,
            PerfilId = perfilId,
            Ativo = true,
        };

        novoUsuario.PasswordHash = _hasher.HashPassword(novoUsuario, senhaInicial);

        _db.Users.Add(novoUsuario);
        await _db.SaveChangesAsync();

        return UserOperationResult.Sucesso;
    }

    public async Task<UserOperationResult> EditarAsync(int id, string displayName, int perfilId, string? novaSenha)
    {
        var user = await _db.Users.FindAsync(id);

        if (user is null)
        {
            return UserOperationResult.NaoEncontrado;
        }

        displayName = displayName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return UserOperationResult.DadosInvalidos;
        }

        if (!string.IsNullOrWhiteSpace(novaSenha) && novaSenha.Length < 8)
        {
            return UserOperationResult.SenhaInvalida;
        }

        if (!await _db.Perfis.AnyAsync(p => p.Id == perfilId))
        {
            return UserOperationResult.DadosInvalidos;
        }

        if (user.PerfilId != perfilId && await EhUltimoAdministradorAtivoAsync(user))
        {
            return UserOperationResult.UltimoAdministrador;
        }

        user.DisplayName = displayName;
        user.PerfilId = perfilId;

        if (!string.IsNullOrWhiteSpace(novaSenha))
        {
            user.PasswordHash = _hasher.HashPassword(user, novaSenha);
        }

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

    public async Task<bool> RedefinirSenhaAsync(string username, string novaSenha)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());

        if (user is null)
        {
            return false;
        }

        user.PasswordHash = _hasher.HashPassword(user, novaSenha);
        user.Ativo = true;

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
