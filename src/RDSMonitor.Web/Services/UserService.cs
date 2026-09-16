using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Services;

public class UserService : IUserService
{
    private readonly AppDbContext _db;
    private readonly PasswordHasher<AppUser> _hasher = new();

    public UserService(AppDbContext db)
    {
        _db = db;
    }

    // TODO: quando a integração com Active Directory / Microsoft Entra ID
    // for implementada (conforme o escopo do projeto), este método deve
    // validar a senha contra o diretório em vez do hash local — ou usar o
    // hash local apenas como alternativa para contas de serviço.
    public async Task<AppUser?> ValidarCredenciaisAsync(string username, string password)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u =>
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
        return await _db.Users.OrderBy(u => u.Username).ToListAsync();
    }

    public async Task<AppUser?> ObterPorIdAsync(int id)
    {
        return await _db.Users.FindAsync(id);
    }

    public async Task<UserOperationResult> CriarAsync(string username, string displayName, UserProfile profile, string senhaInicial)
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

        var jaExiste = await _db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower());

        if (jaExiste)
        {
            return UserOperationResult.UsuarioJaExiste;
        }

        var novoUsuario = new AppUser
        {
            Username = username,
            DisplayName = displayName,
            Profile = profile,
            Ativo = true,
        };

        novoUsuario.PasswordHash = _hasher.HashPassword(novoUsuario, senhaInicial);

        _db.Users.Add(novoUsuario);
        await _db.SaveChangesAsync();

        return UserOperationResult.Sucesso;
    }

    public async Task<UserOperationResult> EditarAsync(int id, string displayName, UserProfile profile, string? novaSenha)
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

        user.DisplayName = displayName;
        user.Profile = profile;

        if (!string.IsNullOrWhiteSpace(novaSenha))
        {
            user.PasswordHash = _hasher.HashPassword(user, novaSenha);
        }

        await _db.SaveChangesAsync();

        return UserOperationResult.Sucesso;
    }

    public async Task<UserOperationResult> AlternarAtivoAsync(int id)
    {
        var user = await _db.Users.FindAsync(id);

        if (user is null)
        {
            return UserOperationResult.NaoEncontrado;
        }

        user.Ativo = !user.Ativo;
        await _db.SaveChangesAsync();

        return UserOperationResult.Sucesso;
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
        await _db.SaveChangesAsync();

        return true;
    }
}
