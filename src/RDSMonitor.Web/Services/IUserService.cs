using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Services;

public enum PasswordChangeResult
{
    Sucesso,
    CredenciaisInvalidas,
    NovaSenhaInvalida,
}

public interface IUserService
{
    Task<AppUser?> ValidarCredenciaisAsync(string username, string password);

    Task RegistrarLoginAsync(int userId);

    Task<PasswordChangeResult> AlterarSenhaAsync(string username, string senhaAtual, string novaSenha);
}
