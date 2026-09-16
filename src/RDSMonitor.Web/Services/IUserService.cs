using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Services;

public enum PasswordChangeResult
{
    Sucesso,
    CredenciaisInvalidas,
    NovaSenhaInvalida,
}

public enum UserOperationResult
{
    Sucesso,
    NaoEncontrado,
    UsuarioJaExiste,
    SenhaInvalida,
    DadosInvalidos,
}

public interface IUserService
{
    Task<AppUser?> ValidarCredenciaisAsync(string username, string password);

    Task RegistrarLoginAsync(int userId);

    Task<PasswordChangeResult> AlterarSenhaAsync(string username, string senhaAtual, string novaSenha);

    /// <summary>Lista todos os usuários do sistema (ativos e desabilitados), ordenados por login.</summary>
    Task<List<AppUser>> ListarAsync();

    Task<AppUser?> ObterPorIdAsync(int id);

    /// <summary>Cria um novo usuário do painel. Retorna o motivo caso não seja possível.</summary>
    Task<UserOperationResult> CriarAsync(string username, string displayName, UserProfile profile, string senhaInicial);

    /// <summary>Atualiza nome de exibição e perfil; redefine a senha apenas se <paramref name="novaSenha"/> vier preenchida.</summary>
    Task<UserOperationResult> EditarAsync(int id, string displayName, UserProfile profile, string? novaSenha);

    /// <summary>Habilita ou desabilita o usuário (alterna o campo Ativo).</summary>
    Task<UserOperationResult> AlternarAtivoAsync(int id);

    /// <summary>
    /// Redefine a senha de um usuário sem exigir a senha atual — usada apenas
    /// pela flag de manutenção "--reset-senha-admin" (ver Program.cs), para
    /// recuperar o acesso quando a senha do admin é esquecida.
    /// </summary>
    Task<bool> RedefinirSenhaAsync(string username, string novaSenha);
}
