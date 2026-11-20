using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models;
using RDSMonitor.Web.Services;

namespace RDSMonitor.Web.Security;

/// <summary>
/// Login com Microsoft 365 (Entra ID, OpenID Connect). Tenant, ID do aplicativo e
/// chave vêm da tela Configurações — lidos quando o esquema é usado e recarregados
/// sempre que a configuração é salva (<see cref="Recarregar"/>).
/// </summary>
public sealed class ConfigurarMicrosoft365 : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public const string Esquema = "Microsoft365";

    private readonly IServiceScopeFactory _scopes;

    public ConfigurarMicrosoft365(IServiceScopeFactory scopes) => _scopes = scopes;

    public void Configure(string? name, OpenIdConnectOptions o)
    {
        if (name != Esquema) return;

        using var scope = _scopes.CreateScope();
        var config  = scope.ServiceProvider.GetRequiredService<IConfiguracaoService>();
        var m365    = config.M365Async().GetAwaiter().GetResult();
        var segredo = config.SegredoM365Async().GetAwaiter().GetResult();

        // Sem configuração ainda: valores neutros só para o esquema existir (o botão fica escondido)
        var tenant = string.IsNullOrWhiteSpace(m365.TenantId) ? "organizations" : m365.TenantId.Trim();
        o.Authority    = $"https://login.microsoftonline.com/{tenant}/v2.0";
        o.ClientId     = string.IsNullOrWhiteSpace(m365.ClientId) ? "nao-configurado" : m365.ClientId.Trim();
        o.ClientSecret = segredo;
    }

    public void Configure(OpenIdConnectOptions o) => Configure(Options.DefaultName, o);

    /// <summary>Descarta as opções em cache para a próxima tentativa usar a configuração nova.</summary>
    public static void Recarregar(IServiceProvider sp) =>
        sp.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>().TryRemove(Esquema);
}

/// <summary>Eventos do login Microsoft 365: liga a conta da Microsoft ao usuário do portal pelo e-mail.</summary>
public static class Microsoft365Login
{
    public static async Task AoValidarTokenAsync(TokenValidatedContext ctx)
    {
        var sp     = ctx.HttpContext.RequestServices;
        var db     = sp.GetRequiredService<AppDbContext>();
        var auth   = sp.GetRequiredService<IAutenticacaoService>();
        var ip     = ctx.HttpContext.Connection.RemoteIpAddress?.ToString();
        var claims = ctx.Principal!;

        var email = claims.FindFirst("preferred_username")?.Value
                    ?? claims.FindFirst("email")?.Value
                    ?? claims.FindFirst("upn")?.Value;

        var e = email?.Trim().ToLower();
        var user = e is null ? null : await db.Users.Include(u => u.Perfil)
            .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == e
                                   && u.TipoAutenticacao == TipoAutenticacao.Microsoft365);

        if (user is null || !user.Ativo)
        {
            await auth.RegistrarEventoAsync(email ?? "(sem e-mail)", ip,
                user is null ? AcessoResultado.UsuarioDesconhecido : AcessoResultado.UsuarioDesabilitado,
                user is null ? "Microsoft 365: nenhum usuário do portal com esse e-mail (tipo Microsoft 365)" : "Microsoft 365");
            ctx.HandleResponse();
            ctx.Response.Redirect(user is null ? "/login?erro=m365-usuario" : "/login?erro=1");
            return;
        }

        user.UltimoLogin       = DateTime.UtcNow;
        user.AccessFailedCount = 0;
        user.LockoutEnd        = null;
        await db.SaveChangesAsync();
        await auth.RegistrarEventoAsync(user.Username, ip, AcessoResultado.Sucesso, "Microsoft 365");

        // O cookie do portal guarda as mesmas informações de um login local
        ctx.Principal = SessaoPortal.Principal(user, "Microsoft 365");
        sp.GetRequiredService<PresencaService>().Entrou(user.Username, user.DisplayName, ip);
    }

    public static Task AoFalharAsync(RemoteFailureContext ctx)
    {
        ctx.HttpContext.RequestServices.GetRequiredService<ILogger<ConfigurarMicrosoft365>>()
            .LogWarning(ctx.Failure, "Falha no login Microsoft 365: {Msg}", ctx.Failure?.Message);
        ctx.HandleResponse();
        ctx.Response.Redirect("/login?erro=m365");
        return Task.CompletedTask;
    }
}

/// <summary>Monta o usuário logado (cookie) — o mesmo para login local e Microsoft 365.</summary>
public static class SessaoPortal
{
    public static ClaimsPrincipal Principal(AppUser user, string origem)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name,           user.DisplayName),
            new(ClaimTypes.Role,           user.Perfil?.Nome ?? "Sem perfil"),
            new("username",                user.Username),
            new("origem-login",            origem),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    /// <summary>Primeira página que o perfil pode abrir.</summary>
    public static async Task<string> PaginaInicialAsync(IServiceProvider sp, ClaimsPrincipal principal)
    {
        var perms = await sp.GetRequiredService<IPermissaoService>().DoUsuarioAsync(principal);
        return new[]
        {
            (Permissoes.DashboardVer, "/"), (Permissoes.SessoesVer, "/sessoes"), (Permissoes.ServidoresVer, "/servidores"),
            (Permissoes.RelatoriosVer, "/relatorios"), (Permissoes.AuditoriaVer, "/auditoria"),
            (Permissoes.UsuariosGerenciar, "/usuarios"), (Permissoes.PerfisGerenciar, "/perfis-usuarios"),
            (Permissoes.ConfiguracoesGerenciar, "/configuracoes"),
        }.FirstOrDefault(x => perms.Contains(x.Item1)).Item2 ?? "/acesso-negado";
    }
}
