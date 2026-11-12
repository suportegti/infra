using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Components;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models.Dtos;
using RDSMonitor.Web.Services;
using System.Security.Claims;

// Comando de manutenção "--reset-senha-admin[=NovaSenha]": tratado à parte e
// removido dos args antes de montar o host, para não ser interpretado como
// chave de configuração.
var comandoReset = args.FirstOrDefault(a => a.StartsWith("--reset-senha-admin", StringComparison.OrdinalIgnoreCase));
args = args.Where(a => !a.StartsWith("--reset-senha-admin", StringComparison.OrdinalIgnoreCase)).ToArray();

var builder = WebApplication.CreateBuilder(args);

// ── Banco de dados ────────────────────────────────────────────────────────────
// SQLite para esta versão. Para SQL Server, altere o provider e a connection
// string em appsettings.json (conforme escopo do projeto).
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=rdsmonitor.db";

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));

// ── Autenticação (cookie) ─────────────────────────────────────────────────────
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath          = "/login";
        o.AccessDeniedPath   = "/acesso-negado";
        o.ExpireTimeSpan     = TimeSpan.FromHours(8);
        o.SlidingExpiration  = true;
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
// O AuthenticationStateProvider padrão do Blazor Server (registrado por
// AddInteractiveServerComponents) já lê o usuário do cookie no início do
// circuito — funciona tanto nas páginas estáticas quanto nas interativas.
builder.Services.AddCascadingAuthenticationState();

// ── Serviços da aplicação ─────────────────────────────────────────────────────
builder.Services.AddScoped<IUserService,    UserService>();
builder.Services.AddScoped<IServerService,  ServerService>();
builder.Services.AddScoped<ICommandService, CommandService>();

// Marca como offline os servidores que pararam de enviar heartbeat.
builder.Services.AddHostedService<ServerHealthMonitor>();

// Interatividade no servidor (SignalR): necessária para @onclick, @bind e
// para os timers de atualização automática das telas de monitoramento.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// ── Pipeline HTTP ─────────────────────────────────────────────────────────────
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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ─────────────────────────────────────────────────────────────────────────────
//  Endpoints de autenticação (portal web)
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/auth/login", async (
        HttpContext http,
        IUserService userService,
        [FromForm] string username,
        [FromForm] string password,
        [FromForm] string? returnUrl) =>
    {
        var user = await userService.ValidarCredenciaisAsync(username, password);
        if (user is null) return Results.Redirect("/login?erro=1");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name,           user.DisplayName),
            new(ClaimTypes.Role,           user.Profile.ToString()),
            new("username",                user.Username),
        };
        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        await userService.RegistrarLoginAsync(user.Id);

        return Results.Redirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
    })
    .DisableAntiforgery();

app.MapPost("/api/auth/logout", async (HttpContext http) =>
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    })
    .DisableAntiforgery();

// Troca de senha autosserviço (tela /alterar-senha): exige usuário + senha
// atual, pois ainda não há AD / Entra ID para uma recuperação por e-mail.
app.MapPost("/api/auth/change-password", async (
        IUserService userService,
        [FromForm] string username,
        [FromForm] string senhaAtual,
        [FromForm] string novaSenha,
        [FromForm] string confirmarSenha) =>
    {
        var usuarioQs = Uri.EscapeDataString(username);

        if (!string.Equals(novaSenha, confirmarSenha, StringComparison.Ordinal))
            return Results.Redirect($"/alterar-senha?erro=confirmacao&usuario={usuarioQs}");

        var resultado = await userService.AlterarSenhaAsync(username, senhaAtual, novaSenha);

        return resultado switch
        {
            PasswordChangeResult.Sucesso           => Results.Redirect("/alterar-senha?sucesso=1"),
            PasswordChangeResult.NovaSenhaInvalida => Results.Redirect($"/alterar-senha?erro=fraca&usuario={usuarioQs}"),
            _                                      => Results.Redirect($"/alterar-senha?erro=credenciais&usuario={usuarioQs}"),
        };
    })
    .DisableAntiforgery();

// ─────────────────────────────────────────────────────────────────────────────
//  Endpoints de usuários (ações de formulário no portal)
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/usuarios/criar", async (
        IUserService userService,
        [FromForm] string username,
        [FromForm] string displayName,
        [FromForm] string profile,
        [FromForm] string senhaInicial) =>
    {
        if (!Enum.TryParse<RDSMonitor.Web.Models.UserProfile>(profile, out var perfil))
            return Results.Redirect("/usuarios?erro=dados-invalidos");

        var resultado = await userService.CriarAsync(username, displayName, perfil, senhaInicial);

        return resultado switch
        {
            UserOperationResult.Sucesso          => Results.Redirect("/usuarios?sucesso=criado"),
            UserOperationResult.UsuarioJaExiste  => Results.Redirect("/usuarios?erro=usuario-existe"),
            UserOperationResult.SenhaInvalida    => Results.Redirect("/usuarios?erro=senha-fraca"),
            _                                    => Results.Redirect("/usuarios?erro=dados-invalidos"),
        };
    })
    .DisableAntiforgery()
    .RequireAuthorization(p => p.RequireRole("Administrador"));

app.MapPost("/api/usuarios/alternar-ativo", async (
        IUserService userService,
        [FromForm] int id) =>
    {
        await userService.AlternarAtivoAsync(id);
        return Results.Redirect("/usuarios?sucesso=status");
    })
    .DisableAntiforgery()
    .RequireAuthorization(p => p.RequireRole("Administrador"));

// ─────────────────────────────────────────────────────────────────────────────
//  API do Agente  (/api/agent/*)
//  Autenticada por chave estática (X-Agent-Key) — tráfego interno/HTTPS.
// ─────────────────────────────────────────────────────────────────────────────
var agentApi = app.MapGroup("/api/agent")
    .AddEndpointFilter(async (ctx, next) =>
    {
        var config      = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedKey = config["AgentSettings:ApiKey"] ?? string.Empty;

        if (string.IsNullOrEmpty(expectedKey))
        {
            // Chave não configurada → bloqueia para forçar configuração explícita
            return Results.Problem("AgentSettings:ApiKey não configurada no servidor.",
                statusCode: 500);
        }

        ctx.HttpContext.Request.Headers.TryGetValue("X-Agent-Key", out var receivedKey);

        if (receivedKey != expectedKey)
            return Results.Unauthorized();

        return await next(ctx);
    });

// POST /api/agent/heartbeat
// O agente envia o estado completo do servidor a cada ciclo.
agentApi.MapPost("/heartbeat", async (
        AgentHeartbeatDto dto,
        IServerService serverService) =>
    {
        if (string.IsNullOrWhiteSpace(dto.ServerName))
            return Results.BadRequest("ServerName é obrigatório.");

        // Só aceita servidores cadastrados e habilitados na tela Servidores.
        return await serverService.ProcessHeartbeatAsync(dto) switch
        {
            HeartbeatResult.Aceito        => Results.Ok(new { received = DateTime.UtcNow }),
            HeartbeatResult.NaoCadastrado => Results.Problem(
                $"Servidor \"{dto.ServerName}\" não está cadastrado no portal.", statusCode: 403),
            _                             => Results.Problem(
                $"Servidor \"{dto.ServerName}\" está desabilitado no portal.", statusCode: 403),
        };
    });

// GET /api/agent/commands/{serverName}
// O agente busca comandos pendentes para executar.
agentApi.MapGet("/commands/{serverName}", async (
        string serverName,
        ICommandService commandService) =>
    {
        var commands = await commandService.GetPendingForServerAsync(serverName);
        return Results.Ok(commands);
    });

// POST /api/agent/commands/{commandId}/result
// O agente reporta o resultado de um comando executado.
agentApi.MapPost("/commands/{commandId:int}/result", async (
        int commandId,
        CommandResultDto dto,
        ICommandService commandService) =>
    {
        await commandService.UpdateResultAsync(commandId, dto.Success, dto.Output, dto.Error);
        return Results.Ok();
    });


// Endpoint de edição de usuário — referenciado em UsuariosEditar.razor
// Adicionado aqui para manter todos os endpoints de formulário juntos.
app.MapPost("/api/usuarios/editar", async (
        IUserService userService,
        [FromForm] int id,
        [FromForm] string displayName,
        [FromForm] string profile,
        [FromForm] string? novaSenha) =>
    {
        if (!Enum.TryParse<RDSMonitor.Web.Models.UserProfile>(profile, out var perfil))
            return Results.Redirect($"/usuarios/editar/{id}?erro=dados-invalidos");

        var resultado = await userService.EditarAsync(id, displayName, perfil, novaSenha);

        return resultado switch
        {
            UserOperationResult.Sucesso       => Results.Redirect("/usuarios?sucesso=editado"),
            UserOperationResult.SenhaInvalida => Results.Redirect($"/usuarios/editar/{id}?erro=senha-fraca"),
            UserOperationResult.NaoEncontrado => Results.Redirect("/usuarios?erro=nao-encontrado"),
            _                                 => Results.Redirect($"/usuarios/editar/{id}?erro=dados-invalidos"),
        };
    })
    .DisableAntiforgery()
    .RequireAuthorization(p => p.RequireRole("Administrador"));

// ─────────────────────────────────────────────────────────────────────────────
//  Banco: criação e seed inicial
// ─────────────────────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Cria o banco se não existir e acrescenta tabelas/índices novos do
    // modelo a um banco existente — nunca apaga dados (ver SchemaUpgrader).
    await SchemaUpgrader.EnsureSchemaAsync(db, log);

    await SeedData.EnsureSeededAsync(db);
}

// "dotnet run -- --reset-senha-admin" redefine a senha do "admin" para
// "Rds@1234" (ou para o valor após "=", mínimo 8 caracteres), reabilita a
// conta e encerra sem subir o servidor web.
if (comandoReset is not null)
{
    var partes    = comandoReset.Split('=', 2);
    var novaSenha = partes.Length == 2 && !string.IsNullOrWhiteSpace(partes[1]) ? partes[1] : "Rds@1234";

    if (novaSenha.Length < 8)
    {
        Console.WriteLine("A nova senha deve ter pelo menos 8 caracteres. Nada foi alterado.");
        return;
    }

    using var scopeReset = app.Services.CreateScope();
    var userServiceReset = scopeReset.ServiceProvider.GetRequiredService<IUserService>();
    var ok = await userServiceReset.RedefinirSenhaAsync("admin", novaSenha);

    Console.WriteLine(ok
        ? $"Senha do usuário 'admin' redefinida para: {novaSenha}"
        : "Usuário 'admin' não encontrado no banco de dados.");
    return;
}

app.Run();
