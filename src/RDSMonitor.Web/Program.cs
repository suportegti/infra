using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Components;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models.Dtos;
using RDSMonitor.Web.Security;
using RDSMonitor.Web.Services;
using System.Security.Claims;
using System.Threading.RateLimiting;

// Comando de manutenção "--reset-senha-admin[=NovaSenha]": tratado à parte e
// removido dos args antes de montar o host, para não ser interpretado como
// chave de configuração.
var comandoReset = args.FirstOrDefault(a => a.StartsWith("--reset-senha-admin", StringComparison.OrdinalIgnoreCase));
args = args.Where(a => !a.StartsWith("--reset-senha-admin", StringComparison.OrdinalIgnoreCase)).ToArray();

// Comando de manutenção "--desbloquear-admin": remove o bloqueio por tentativas
// do usuário "admin" sem alterar a senha (acesso de emergência pelo servidor).
var comandoDesbloquear = args.Any(a => a.Equals("--desbloquear-admin", StringComparison.OrdinalIgnoreCase));
args = args.Where(a => !a.Equals("--desbloquear-admin", StringComparison.OrdinalIgnoreCase)).ToArray();

var builder = WebApplication.CreateBuilder(args);

// ── Banco de dados ────────────────────────────────────────────────────────────
// SQLite para esta versão. Para SQL Server, altere o provider e a connection
// string em appsettings.json (conforme escopo do projeto).
var connectionString = ResolverCaminhoSqlite(
    builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=rdsmonitor.db",
    builder.Environment.ContentRootPath);

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));

// ── Windows Service ───────────────────────────────────────────────────────────
// Sem efeito quando executado no console (dotnet run). Como serviço, o log vai
// para o Visualizador de Eventos (fonte "RDSMonitor.Web").
builder.Services.AddWindowsService(o => o.ServiceName = "RDSMonitor.Web");

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

// Uma policy por permissão (Security/Permissoes.cs); as permissões de cada
// perfil são configuradas na tela "Perfis de usuários".
builder.Services.AddPermissoes();
builder.Services.AddHttpContextAccessor();
// O AuthenticationStateProvider padrão do Blazor Server (registrado por
// AddInteractiveServerComponents) já lê o usuário do cookie no início do
// circuito — funciona tanto nas páginas estáticas quanto nas interativas.
builder.Services.AddCascadingAuthenticationState();

// ── Serviços da aplicação ─────────────────────────────────────────────────────
builder.Services.AddScoped<IUserService,    UserService>();
builder.Services.AddScoped<IServerService,  ServerService>();
builder.Services.AddScoped<ICommandService, CommandService>();
builder.Services.AddScoped<IPerfilService,  PerfilService>();
builder.Services.AddScoped<IAuditoriaService, AuditoriaService>();
builder.Services.AddScoped<IRelatorioService, RelatorioService>();
builder.Services.AddScoped<IAutenticacaoService, AutenticacaoService>();

// Quem está usando o portal agora (tela Usuários do sistema)
builder.Services.AddSingleton<PresencaService>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, PresencaCircuitHandler>();

// Limite de requisições por IP no login: trava ataques de força bruta mesmo que
// o atacante alterne entre vários usuários. O bloqueio por conta (AutenticacaoService)
// cuida das tentativas contra um único usuário.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window      = TimeSpan.FromMinutes(1),
                QueueLimit  = 0,
            }));

    options.OnRejected = async (ctx, _) =>
    {
        var h = ctx.HttpContext;
        try
        {
            h.Request.Form.TryGetValue("username", out var u);
            var auth = h.RequestServices.GetRequiredService<IAutenticacaoService>();
            await auth.RegistrarLimiteDeTaxaAsync(u.ToString(), h.Connection.RemoteIpAddress?.ToString());
        }
        catch { /* registro é best-effort */ }

        if (!h.Response.HasStarted)
        {
            h.Response.StatusCode = StatusCodes.Status302Found;
            h.Response.Headers.Location = "/login?erro=limite";
        }
    };
});

// Marca como offline os servidores que pararam de enviar heartbeat.
builder.Services.AddHostedService<ServerHealthMonitor>();

// Interatividade no servidor (SignalR): necessária para @onclick, @bind e
// para os timers de atualização automática das telas de monitoramento.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o =>
    {
        // Tela que perdeu a conexão (rede, VPN, notebook suspenso) continua
        // guardada no servidor por 10 min, para reconectar sem perder o estado.
        o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
        o.DisconnectedCircuitMaxRetained     = 500;
    })
    .AddHubOptions(o =>
    {
        // Abas em segundo plano têm os timers atrasados pelo navegador: o sinal de
        // vida do cliente pode demorar. Com 30 s (padrão) o servidor derrubava a
        // conexão de telas de monitoramento abertas numa aba escondida.
        // Deve ser o dobro do intervalo de keep-alive do cliente (js/site.js).
        o.ClientTimeoutInterval = TimeSpan.FromMinutes(2);
        o.KeepAliveInterval     = TimeSpan.FromSeconds(15);
        o.HandshakeTimeout      = TimeSpan.FromSeconds(30);
    });

var app = builder.Build();

// ── Pipeline HTTP ─────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

// Cada requisição autenticada marca o usuário como presente (lista "Conectados agora")
app.Use(async (http, next) =>
{
    if (PresencaService.Login(http.User) is { } login)
        http.RequestServices.GetRequiredService<PresencaService>()
            .Viu(login, http.User.Identity?.Name ?? login, http.Connection.RemoteIpAddress?.ToString());
    await next();
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ─────────────────────────────────────────────────────────────────────────────
//  Endpoints de autenticação (portal web)
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/auth/login", async (
        HttpContext http,
        IAutenticacaoService autenticacao,
        [FromForm] string username,
        [FromForm] string password,
        [FromForm] string? returnUrl) =>
    {
        var ip = http.Connection.RemoteIpAddress?.ToString();
        var resultado = await autenticacao.AutenticarAsync(username, password, ip);

        if (resultado.Status == AuthStatus.Bloqueado)
            return Results.Redirect($"/login?erro=bloqueio&min={resultado.BloqueioMinutos}");
        if (resultado.Status == AuthStatus.TrocaDeSenhaExigida && resultado.Usuario is { } pendente)
            return Results.Redirect($"/alterar-senha?obrigatoria=1&usuario={Uri.EscapeDataString(pendente.Username)}");
        if (resultado.Status != AuthStatus.Sucesso || resultado.Usuario is null)
            return Results.Redirect("/login?erro=1");

        var user = resultado.Usuario;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name,           user.DisplayName),
            new(ClaimTypes.Role,           user.Perfil?.Nome ?? "Sem perfil"),
            new("username",                user.Username),
        };
        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        http.RequestServices.GetRequiredService<PresencaService>().Entrou(user.Username, user.DisplayName, ip);

        // Só aceita retorno para uma página do próprio portal
        if (!string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//"))
            return Results.Redirect(returnUrl);

        // Primeira página que o perfil pode abrir
        var perms = await http.RequestServices.GetRequiredService<IPermissaoService>().DoUsuarioAsync(principal);
        var inicio = new[]
        {
            (Permissoes.DashboardVer, "/"), (Permissoes.SessoesVer, "/sessoes"), (Permissoes.ServidoresVer, "/servidores"),
            (Permissoes.RelatoriosVer, "/relatorios"), (Permissoes.AuditoriaVer, "/auditoria"),
            (Permissoes.UsuariosGerenciar, "/usuarios"), (Permissoes.PerfisGerenciar, "/perfis-usuarios"),
        }.FirstOrDefault(x => perms.Contains(x.Item1)).Item2 ?? "/acesso-negado";
        return Results.Redirect(inicio);
    })
    .DisableAntiforgery()
    .RequireRateLimiting("login");

app.MapPost("/api/auth/logout", async (HttpContext http, IAutenticacaoService autenticacao, PresencaService presenca) =>
    {
        if (PresencaService.Login(http.User) is { } login)
        {
            presenca.Saiu(login);
            await autenticacao.RegistrarLogoutAsync(login, http.Connection.RemoteIpAddress?.ToString());
        }
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    })
    .DisableAntiforgery();

// Troca de senha autosserviço (tela /alterar-senha): exige usuário + senha
// atual, pois ainda não há AD / Entra ID para uma recuperação por e-mail.
app.MapPost("/api/auth/change-password", async (
        HttpContext http,
        IUserService userService,
        [FromForm] string username,
        [FromForm] string senhaAtual,
        [FromForm] string novaSenha,
        [FromForm] string confirmarSenha,
        [FromForm] string? obrigatoria) =>
    {
        // Mantém o aviso de "troca obrigatória" quando a tela volta com erro
        var extra     = obrigatoria == "1" ? "&obrigatoria=1" : "";
        var usuarioQs = Uri.EscapeDataString(username) + extra;

        if (!string.Equals(novaSenha, confirmarSenha, StringComparison.Ordinal))
            return Results.Redirect($"/alterar-senha?erro=confirmacao&usuario={usuarioQs}");

        var resultado = await userService.AlterarSenhaAsync(username, senhaAtual, novaSenha,
            http.Connection.RemoteIpAddress?.ToString());

        return resultado switch
        {
            PasswordChangeResult.Sucesso           => Results.Redirect("/alterar-senha?sucesso=1"),
            PasswordChangeResult.NovaSenhaInvalida => Results.Redirect($"/alterar-senha?erro=fraca&usuario={usuarioQs}"),
            PasswordChangeResult.SenhaIgualAtual   => Results.Redirect($"/alterar-senha?erro=igual&usuario={usuarioQs}"),
            PasswordChangeResult.Bloqueado         => Results.Redirect($"/alterar-senha?erro=bloqueio&usuario={usuarioQs}"),
            _                                      => Results.Redirect($"/alterar-senha?erro=credenciais&usuario={usuarioQs}"),
        };
    })
    .DisableAntiforgery()
    .RequireRateLimiting("login");

// ─────────────────────────────────────────────────────────────────────────────
//  Endpoints de usuários (ações de formulário no portal)
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/usuarios/criar", async (
        IUserService userService,
        [FromForm] string username,
        [FromForm] string displayName,
        [FromForm] int perfilId,
        [FromForm] string senhaInicial,
        [FromForm] string? exigirTroca) =>
    {
        var resultado = await userService.CriarAsync(username, displayName, perfilId, senhaInicial,
            exigirTroca: exigirTroca == "true");

        return resultado switch
        {
            UserOperationResult.Sucesso          => Results.Redirect("/usuarios?sucesso=criado"),
            UserOperationResult.UsuarioJaExiste  => Results.Redirect("/usuarios?erro=usuario-existe"),
            UserOperationResult.SenhaInvalida    => Results.Redirect("/usuarios?erro=senha-fraca"),
            _                                    => Results.Redirect("/usuarios?erro=dados-invalidos"),
        };
    })
    .DisableAntiforgery()
    .RequireAuthorization(Permissoes.UsuariosGerenciar);

app.MapPost("/api/usuarios/alternar-ativo", async (
        IUserService userService,
        [FromForm] int id) =>
    {
        return await userService.AlternarAtivoAsync(id) == UserOperationResult.UltimoAdministrador
            ? Results.Redirect("/usuarios?erro=ultimo-admin")
            : Results.Redirect("/usuarios?sucesso=status");
    })
    .DisableAntiforgery()
    .RequireAuthorization(Permissoes.UsuariosGerenciar);

app.MapPost("/api/usuarios/desbloquear", async (
        IUserService userService,
        [FromForm] int id) =>
    {
        return await userService.DesbloquearAsync(id) == UserOperationResult.NaoEncontrado
            ? Results.Redirect("/usuarios?erro=nao-encontrado")
            : Results.Redirect("/usuarios?sucesso=desbloqueado");
    })
    .DisableAntiforgery()
    .RequireAuthorization(Permissoes.UsuariosGerenciar);

// Exportação CSV da auditoria (mesmos filtros da tela /auditoria)
// Exportações. As datas chegam no horário de Brasília (yyyy-MM-ddTHH:mm[:ss[.fff]]) e são
// lidas como texto para não sofrerem a conversão automática de fuso do model binding.
static DateTime? DataLocal(string? s) =>
    DateTime.TryParseExact(s, new[] { "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd" },
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
        ? DateTime.SpecifyKind(d, DateTimeKind.Unspecified) : null;

static string NomeAcao(CommandType a) => RDSMonitor.Web.Components.Pages.Auditoria.NomeAcao(a);
static string NomeStatus(CommandStatus s) => s switch
{
    CommandStatus.Pending   => "Aguardando agente",
    CommandStatus.Executing => "Em execução",
    CommandStatus.Completed => "Concluído",
    CommandStatus.Failed    => "Falhou",
    _                       => s.ToString(),
};

static async Task<List<AuditoriaAcao>> PesquisarAuditoriaAsync(IAuditoriaService auditoria, HttpRequest req)
{
    var q = req.Query;
    var (itens, _) = await auditoria.PesquisarAsync(new FiltroAuditoria
    {
        DeLocal  = DataLocal(q["de"]),
        AteLocal = DataLocal(q["ate"]),
        Acao     = Enum.TryParse<CommandType>(q["acao"], out var a) ? a : null,
        Status   = Enum.TryParse<CommandStatus>(q["status"], out var st) ? st : null,
        Operador = q["operador"], Servidor = q["servidor"], Alvo = q["alvo"],
        Limite   = 10_000,
    });
    return itens;
}

// GET /api/auditoria/csv
app.MapGet("/api/auditoria/csv", async (IAuditoriaService auditoria, HttpRequest req) =>
    {
        var itens = await PesquisarAuditoriaAsync(auditoria, req);

        static string C(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Data/hora;Operador;Nome;Perfil;Acao;Servidor;Sessao;Usuario alvo;Processo;PID;Detalhes;Status;Concluido em;Resultado");
        foreach (var a in itens)
            sb.AppendLine(string.Join(";",
                C(Horario.Fmt(a.DataHora, "dd/MM/yyyy HH:mm:ss")), C(a.OperadorLogin), C(a.OperadorNome),
                C(a.OperadorPerfil), C(NomeAcao(a.Acao)), C(a.ServidorNome), C(a.SessaoId?.ToString()),
                C(a.UsuarioAlvo), C(a.Processo), C(a.Pid?.ToString()), C(a.Detalhes), C(NomeStatus(a.Status)),
                C(Horario.Fmt(a.ConcluidoEm, "dd/MM/yyyy HH:mm:ss", "")), C(a.Resultado)));

        // BOM para o Excel abrir com acentuação correta
        var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", $"auditoria-{Horario.Agora:yyyyMMdd-HHmm}.csv");
    })
    .RequireAuthorization(Permissoes.AuditoriaVer);

// GET /api/auditoria/xlsx
app.MapGet("/api/auditoria/xlsx", async (IAuditoriaService auditoria, HttpRequest req) =>
    {
        var itens = await PesquisarAuditoriaAsync(auditoria, req);
        var colunas = new XlsxColuna[]
        {
            new("Data/hora", XlsxTipo.DataHora, 20), new("Operador", Largura: 16), new("Nome", Largura: 24),
            new("Perfil", Largura: 16), new("Ação", Largura: 18), new("Servidor", Largura: 16),
            new("Sessão", XlsxTipo.Numero, 9), new("Usuário alvo", Largura: 20), new("Processo", Largura: 20),
            new("PID", XlsxTipo.Numero, 9), new("Detalhes", Largura: 40), new("Resultado", Largura: 16),
            new("Concluído em", XlsxTipo.DataHora, 20), new("Retorno do agente", Largura: 50),
        };
        var linhas = itens.Select(a => new object?[]
        {
            Horario.Br(a.DataHora), a.OperadorLogin, a.OperadorNome, a.OperadorPerfil, NomeAcao(a.Acao),
            a.ServidorNome, a.SessaoId, a.UsuarioAlvo, a.Processo, a.Pid, a.Detalhes, NomeStatus(a.Status),
            Horario.Br(a.ConcluidoEm), a.Resultado,
        });
        return Results.File(Xlsx.Gerar("Auditoria", colunas, linhas), Xlsx.ContentType,
            $"auditoria-{Horario.Agora:yyyyMMdd-HHmm}.xlsx");
    })
    .RequireAuthorization(Permissoes.AuditoriaVer);

// GET /api/relatorios/eventos.xlsx (mesmos filtros e opções da tela Relatórios)
static Task<(List<SessionHistory>, bool)> PesquisarRelatorioAsync(IRelatorioService relatorios, HttpRequest req)
{
    var q = req.Query;
    return relatorios.PesquisarHistoricoAsync(new FiltroRelatorio
    {
        DeLocal  = DataLocal(q["de"]),
        AteLocal = DataLocal(q["ate"]),
        Servidor = q["servidor"], Cliente = q["cliente"], Usuario = q["usuario"], Situacao = q["situacao"],
        Limite   = 50_000,
    });
}

app.MapGet("/api/relatorios/eventos.xlsx", async (IRelatorioService relatorios, HttpRequest req) =>
    {
        // Espelho da tabela da tela Relatórios (mesmas linhas, ordem e textos); a Origem
        // sai em duas colunas, Hostname e IP, para facilitar filtros e análises no Excel
        var (itens, _) = await PesquisarRelatorioAsync(relatorios, req);
        var linhasEventos = relatorios.Eventos(itens,
            Horario.ParaUtc(DataLocal(req.Query["de"])), Horario.ParaUtc(DataLocal(req.Query["ate"])),
            incluirSecundarios: req.Query["secundarios"] == "1");
        if (req.Query["ordem"] == "recentes") linhasEventos.Reverse();

        var colunas = new XlsxColuna[]
        {
            new("Data/hora", XlsxTipo.DataHora, 20), new("Servidor", Largura: 24), new("Usuário", Largura: 24),
            new("Sessão", XlsxTipo.Numero, 9), new("Hostname", Largura: 20), new("IP", Largura: 16),
            new("Duração", Largura: 22),
            new("Status", Largura: 16), new("Detalhe", Largura: 60),
        };
        var linhas = linhasEventos.Select(l => new object?[]
        {
            Horario.Br(l.DataHora), l.TextoServidor, l.Sessao.Username, l.Sessao.RdsSessionId,
            l.Sessao.ClientName, l.IpOrigem, l.TextoDuracao, l.Status, l.TextoDetalhe,
        });
        return Results.File(Xlsx.Gerar("Histórico de sessões", colunas, linhas), Xlsx.ContentType,
            $"historico-sessoes-{Horario.Agora:yyyyMMdd-HHmm}.xlsx");
    })
    .RequireAuthorization(Permissoes.RelatoriosVer);

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

// GET /api/agent/ping/{serverName}
// Teste de conexão usado pelo instalador do agente: confirma que a URL e a
// chave estão corretas e informa se o servidor já foi cadastrado no portal.
agentApi.MapGet("/ping/{serverName}", async (string serverName, AppDbContext db) =>
    {
        var nome     = serverName.ToLower();
        var servidor = await db.Servers.AsNoTracking()
            .Where(s => s.Name.ToLower() == nome)
            .Select(s => new { s.Name, s.IsEnabled })
            .FirstOrDefaultAsync();

        return Results.Ok(new
        {
            ok         = true,
            servidor   = serverName,
            cadastrado = servidor is not null,
            habilitado = servidor?.IsEnabled ?? false,
            horaUtc    = DateTime.UtcNow,
        });
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
        [FromForm] int perfilId,
        [FromForm] string? novaSenha,
        [FromForm] string? exigirTroca) =>
    {
        var resultado = await userService.EditarAsync(id, displayName, perfilId, novaSenha,
            exigirTroca: exigirTroca == "true");

        return resultado switch
        {
            UserOperationResult.Sucesso       => Results.Redirect("/usuarios?sucesso=editado"),
            UserOperationResult.SenhaInvalida => Results.Redirect($"/usuarios/editar/{id}?erro=senha-fraca"),
            UserOperationResult.NaoEncontrado => Results.Redirect("/usuarios?erro=nao-encontrado"),
            UserOperationResult.UltimoAdministrador => Results.Redirect($"/usuarios/editar/{id}?erro=ultimo-admin"),
            _                                 => Results.Redirect($"/usuarios/editar/{id}?erro=dados-invalidos"),
        };
    })
    .DisableAntiforgery()
    .RequireAuthorization(Permissoes.UsuariosGerenciar);

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

    await SeedData.EnsureSeededAsync(db, demonstracao: app.Environment.IsDevelopment());
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
    // Com a senha padrão (pública), o admin precisa definir a dele no próximo acesso
    var senhaPadrao = partes.Length < 2 || string.IsNullOrWhiteSpace(partes[1]);
    var ok = await userServiceReset.RedefinirSenhaAsync("admin", novaSenha, exigirTroca: senhaPadrao);

    Console.WriteLine(ok
        ? $"Senha do usuário 'admin' redefinida para: {novaSenha}" + (senhaPadrao ? " (a troca será exigida no próximo login)" : "")
        : "Usuário 'admin' não encontrado no banco de dados.");
    return;
}

// "dotnet run -- --desbloquear-admin" remove o bloqueio por tentativas do admin.
if (comandoDesbloquear)
{
    using var scopeUnlock = app.Services.CreateScope();
    var userServiceUnlock = scopeUnlock.ServiceProvider.GetRequiredService<IUserService>();
    var ok = await userServiceUnlock.DesbloquearAsync("admin");

    Console.WriteLine(ok
        ? "Bloqueio do usuário 'admin' removido. Ele já pode entrar com a senha atual."
        : "Usuário 'admin' não encontrado no banco de dados.");
    return;
}

app.Run();

// Caminho relativo do SQLite ("Data Source=rdsmonitor.db") passa a ser relativo
// à pasta da aplicação. Como serviço do Windows o diretório atual é System32,
// e o banco iria parar lá.
static string ResolverCaminhoSqlite(string connectionString, string contentRoot)
{
    var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
    if (!string.IsNullOrWhiteSpace(csb.DataSource)
        && csb.DataSource != ":memory:"
        && !Path.IsPathRooted(csb.DataSource))
    {
        csb.DataSource = Path.GetFullPath(Path.Combine(contentRoot, csb.DataSource));
    }

    var pasta = Path.GetDirectoryName(csb.DataSource);
    if (!string.IsNullOrEmpty(pasta)) Directory.CreateDirectory(pasta);

    return csb.ToString();
}
