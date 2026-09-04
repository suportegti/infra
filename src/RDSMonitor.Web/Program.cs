using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Components;
using RDSMonitor.Web.Data;
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

// Garante a criação do banco SQLite e insere os usuários de demonstração
// (um por perfil) na primeira execução.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    await SeedData.EnsureSeededAsync(db);
}

app.Run();
