using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Services;

public enum PasswordChangeResult
{
    Sucesso,
    CredenciaisInvalidas,
    NovaSenhaInvalida,
    /// <summary>Conta bloqueada por tentativas (a troca também conta tentativas).</summary>
    Bloqueado,
    /// <summary>A nova senha é igual à atual.</summary>
    SenhaIgualAtual,
}

public enum UserOperationResult
{
    Sucesso,
    NaoEncontrado,
    UsuarioJaExiste,
    SenhaInvalida,
    DadosInvalidos,
    /// <summary>A operação deixaria o sistema sem nenhum administrador ativo.</summary>
    UltimoAdministrador,
}

public interface IUserService
{
    Task<AppUser?> ValidarCredenciaisAsync(string username, string password);

    Task RegistrarLoginAsync(int userId);

    /// <summary>
    /// Troca de senha pelo próprio usuário (exige a senha atual). Falhas contam
    /// para o bloqueio por tentativas, como no login. Limpa a exigência de troca.
    /// </summary>
    Task<PasswordChangeResult> AlterarSenhaAsync(string username, string senhaAtual, string novaSenha, string? ip = null);

    /// <summary>Histórico de acessos (logins, falhas, saídas, trocas de senha) de um usuário. Datas em UTC.</summary>
    Task<(List<RDSMonitor.Web.Data.Entities.AcessoLog> Itens, bool Truncado)> ListarAcessosAsync(
        string username, DateTime? deUtc, DateTime? ateUtc, int limite = 500);

    /// <summary>Lista todos os usuários do sistema (ativos e desabilitados), ordenados por login.</summary>
    Task<List<AppUser>> ListarAsync();

    Task<AppUser?> ObterPorIdAsync(int id);

    /// <summary>Cria um novo usuário do painel. Retorna o motivo caso não seja possível.</summary>
    Task<UserOperationResult> CriarAsync(string username, string displayName, int perfilId, string senhaInicial, bool exigirTroca = true);

    /// <summary>Atualiza nome de exibição e perfil; redefine a senha apenas se <paramref name="novaSenha"/> vier preenchida.</summary>
    Task<UserOperationResult> EditarAsync(int id, string displayName, int perfilId, string? novaSenha, bool exigirTroca = false);

    /// <summary>Habilita ou desabilita o usuário (alterna o campo Ativo).</summary>
    Task<UserOperationResult> AlternarAtivoAsync(int id);

    /// <summary>Remove o bloqueio por tentativas de um usuário (zera contador e expira o bloqueio).</summary>
    Task<UserOperationResult> DesbloquearAsync(int id);

    /// <summary>Remove o bloqueio por tentativas pelo login — usado pela flag "--desbloquear-admin".</summary>
    Task<bool> DesbloquearAsync(string username);

    /// <summary>
    /// Redefine a senha de um usuário sem exigir a senha atual — usada apenas
    /// pela flag de manutenção "--reset-senha-admin" (ver Program.cs), para
    /// recuperar o acesso quando a senha do admin é esquecida.
    /// </summary>
    Task<bool> RedefinirSenhaAsync(string username, string novaSenha, bool exigirTroca = false);
}
