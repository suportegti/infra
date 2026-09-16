using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Components;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Models;
using RDSMonitor.Web.Services;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Banco de dados: SQLite para esta primeira versão (conforme combinado no
// escopo do projeto). Trocar o provider para SQL Server quando o volume de
// servidores/usuários justificar.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=rdsmonitor.db";

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

// Autenticação baseada em cookie. Este é o ponto de extensão para a futura
// integração com Active Directory (System.DirectoryServices.AccountManagement)
// ou Microsoft Entra ID (Microsoft.Identity.Web), conforme o escopo do projeto.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/acesso-negado";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuthenticationStateProvider, CookieAuthenticationStateProvider>();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddScoped<IUserService, UserService>();

builder.Services.AddRazorComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorComponents<App>();

// Endpoints de autenticação. Ficam fora do circuito do Blazor de propósito:
// é assim que o cookie de autenticação é gravado corretamente na resposta
// HTTP a partir de um <form method="post"> comum na tela de login.
app.MapPost("/api/auth/login", async (
        HttpContext http,
        IUserService userService,
        [FromForm] string username,
        [FromForm] string password,
        [FromForm] string? returnUrl) =>
    {
        var user = await userService.ValidarCredenciaisAsync(username, password);

        if (user is null)
        {
            return Results.Redirect("/login?erro=1");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Profile.ToString()),
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        await userService.RegistrarLoginAsync(user.Id);

        var destino = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
        return Results.Redirect(destino);
    })
    .DisableAntiforgery();

app.MapPost("/api/auth/logout", async (HttpContext http) =>
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    })
    .DisableAntiforgery();

// Troca de senha autosserviço: exige usuário + senha atual, pois ainda não
// há Active Directory / Entra ID integrado (sem isso não há como oferecer
// uma recuperação por e-mail de verdade).
app.MapPost("/api/auth/change-password", async (
        IUserService userService,
        [FromForm] string username,
        [FromForm] string senhaAtual,
        [FromForm] string novaSenha,
        [FromForm] string confirmarSenha) =>
    {
        if (!string.Equals(novaSenha, confirmarSenha, StringComparison.Ordinal))
        {
            return Results.Redirect($"/alterar-senha?erro=confirmacao&usuario={Uri.EscapeDataString(username)}");
        }

        var resultado = await userService.AlterarSenhaAsync(username, senhaAtual, novaSenha);

        return resultado switch
        {
            RDSMonitor.Web.Services.PasswordChangeResult.Sucesso => Results.Redirect("/alterar-senha?sucesso=1"),
            RDSMonitor.Web.Services.PasswordChangeResult.NovaSenhaInvalida => Results.Redirect($"/alterar-senha?erro=fraca&usuario={Uri.EscapeDataString(username)}"),
            _ => Results.Redirect($"/alterar-senha?erro=credenciais&usuario={Uri.EscapeDataString(username)}"),
        };
    })
    .DisableAntiforgery();

// Administração de usuários do sistema (tela /usuarios). Somente
// Administrador — reforçado aqui também, não só na página, pois estes são
// endpoints HTTP chamáveis diretamente.
app.MapPost("/api/usuarios/criar", async (
        IUserService userService,
        [FromForm] string username,
        [FromForm] string displayName,
        [FromForm] string profile,
        [FromForm] string senhaInicial) =>
    {
        if (!Enum.TryParse<UserProfile>(profile, ignoreCase: true, out var perfil))
        {
            return Results.Redirect("/usuarios?erro=dados-invalidos");
        }

        var resultado = await userService.CriarAsync(username, displayName, perfil, senhaInicial);

        return resultado switch
        {
            UserOperationResult.Sucesso => Results.Redirect("/usuarios?sucesso=criado"),
            UserOperationResult.UsuarioJaExiste => Results.Redirect("/usuarios?erro=usuario-existe"),
            UserOperationResult.SenhaInvalida => Results.Redirect("/usuarios?erro=senha-fraca"),
            _ => Results.Redirect("/usuarios?erro=dados-invalidos"),
        };
    })
    .RequireAuthorization(policy => policy.RequireRole("Administrador"))
    .DisableAntiforgery();

app.MapPost("/api/usuarios/editar", async (
        IUserService userService,
        [FromForm] int id,
        [FromForm] string displayName,
        [FromForm] string profile,
        [FromForm] string? novaSenha) =>
    {
        if (!Enum.TryParse<UserProfile>(profile, ignoreCase: true, out var perfil))
        {
            return Results.Redirect($"/usuarios/editar/{id}?erro=dados-invalidos");
        }

        var resultado = await userService.EditarAsync(id, displayName, perfil, novaSenha);

        return resultado switch
        {
            UserOperationResult.Sucesso => Results.Redirect("/usuarios?sucesso=editado"),
            UserOperationResult.SenhaInvalida => Results.Redirect($"/usuarios/editar/{id}?erro=senha-fraca"),
            UserOperationResult.NaoEncontrado => Results.Redirect("/usuarios?erro=nao-encontrado"),
            _ => Results.Redirect($"/usuarios/editar/{id}?erro=dados-invalidos"),
        };
    })
    .RequireAuthorization(policy => policy.RequireRole("Administrador"))
    .DisableAntiforgery();

app.MapPost("/api/usuarios/alternar-ativo", async (
        IUserService userService,
        [FromForm] int id) =>
    {
        await userService.AlternarAtivoAsync(id);
        return Results.Redirect("/usuarios?sucesso=status");
    })
    .RequireAuthorization(policy => policy.RequireRole("Administrador"))
    .DisableAntiforgery();

// Garante a criação do banco SQLite e insere os usuários de demonstração
// (um por perfil) na primeira execução.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    await SeedData.EnsureSeededAsync(db);
}

// Comando de manutenção: "dotnet run -- --reset-senha-admin" redefine a
// senha do usuário "admin" para "Rds@1234" (ou para o valor passado após o
// "=", ex.: --reset-senha-admin=NovaSenha123) sem apagar mais nada do
// banco, e reabilita a conta caso esteja desabilitada. Roda uma vez e
// encerra — não sobe o servidor web.
var comandoReset = args.FirstOrDefault(a => a.StartsWith("--reset-senha-admin", StringComparison.OrdinalIgnoreCase));

if (comandoReset is not null)
{
    var partesComando = comandoReset.Split('=', 2);
    var novaSenhaAdmin = partesComando.Length == 2 && !string.IsNullOrWhiteSpace(partesComando[1])
        ? partesComando[1]
        : "Rds@1234";

    using var scopeReset = app.Services.CreateScope();
    var userServiceReset = scopeReset.ServiceProvider.GetRequiredService<IUserService>();
    var sucessoReset = await userServiceReset.RedefinirSenhaAsync("admin", novaSenhaAdmin);

    Console.WriteLine(sucessoReset
        ? $"Senha do usuário 'admin' redefinida para: {novaSenhaAdmin}"
        : "Usuário 'admin' não encontrado no banco de dados.");

    return;
}

app.Run();
