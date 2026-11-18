using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;

namespace RDSMonitor.Web.Security;

/// <summary>
/// Resolve as permissões do usuário logado a partir do perfil gravado no banco.
/// As permissões NÃO ficam no cookie: alterar um perfil (ou desabilitar um
/// usuário) vale em até alguns segundos, sem precisar sair e entrar de novo.
/// </summary>
public interface IPermissaoService
{
    Task<IReadOnlySet<string>> DoUsuarioAsync(ClaimsPrincipal usuario);

    Task<bool> PossuiAsync(ClaimsPrincipal usuario, string permissao);

    /// <summary>Descarta o cache — chamar após alterar perfis ou usuários.</summary>
    void Invalidar();
}

public sealed class PermissaoService : IPermissaoService
{
    private static readonly TimeSpan Validade = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlySet<string> Nenhuma = new HashSet<string>();

    private readonly IServiceScopeFactory _scopes;
    private readonly ConcurrentDictionary<int, (DateTime Expira, IReadOnlySet<string> Permissoes)> _cache = new();

    public PermissaoService(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<IReadOnlySet<string>> DoUsuarioAsync(ClaimsPrincipal usuario)
    {
        if (usuario.Identity?.IsAuthenticated != true) return Nenhuma;
        if (!int.TryParse(usuario.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return Nenhuma;

        if (_cache.TryGetValue(id, out var item) && item.Expira > DateTime.UtcNow)
            return item.Permissoes;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await db.Users.AsNoTracking()
            .Include(u => u.Perfil)
            .FirstOrDefaultAsync(u => u.Id == id);

        // Usuário excluído/desabilitado ou sem perfil: nenhuma permissão
        var permissoes = user is { Ativo: true, Perfil: not null } ? user.Perfil.ListaPermissoes : Nenhuma;

        _cache[id] = (DateTime.UtcNow.Add(Validade), permissoes);
        return permissoes;
    }

    public async Task<bool> PossuiAsync(ClaimsPrincipal usuario, string permissao)
        => (await DoUsuarioAsync(usuario)).Contains(permissao);

    public void Invalidar() => _cache.Clear();
}

/// <summary>Requisito "o usuário tem a permissão X" — uma policy por permissão.</summary>
public sealed record PermissaoRequirement(string Codigo) : IAuthorizationRequirement;

public sealed class PermissaoHandler : AuthorizationHandler<PermissaoRequirement>
{
    private readonly IPermissaoService _permissoes;

    public PermissaoHandler(IPermissaoService permissoes) => _permissoes = permissoes;

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissaoRequirement requirement)
    {
        if (await _permissoes.PossuiAsync(context.User, requirement.Codigo))
            context.Succeed(requirement);
    }
}

public static class PermissaoExtensions
{
    /// <summary>Registra o serviço de permissões e uma policy para cada permissão do catálogo.</summary>
    public static IServiceCollection AddPermissoes(this IServiceCollection services)
    {
        services.AddSingleton<IPermissaoService, PermissaoService>();
        services.AddSingleton<IAuthorizationHandler, PermissaoHandler>();
        services.AddAuthorization(o =>
        {
            foreach (var codigo in Permissoes.Todas)
                o.AddPolicy(codigo, p => p.RequireAuthenticatedUser().AddRequirements(new PermissaoRequirement(codigo)));
        });
        return services;
    }
}
