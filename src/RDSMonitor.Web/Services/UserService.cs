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
}
