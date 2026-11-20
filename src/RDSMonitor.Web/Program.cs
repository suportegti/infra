using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Components;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models;
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

// Pasta de dados (a do banco: data\ na instalação), preservada pelo instalador nas atualizações
var pastaDados = Path.GetDirectoryName(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource)
                 ?? builder.Environment.ContentRootPath;

// Chaves de criptografia (cookies de login e segredos da tela Configurações) em data\chaves,
// protegidas pelo DPAPI da máquina — sobrevivem a reinstalações e a troca da conta do serviço.
var protecao = builder.Services.AddDataProtection()
    .SetApplicationName("RDSMonitor")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(pastaDados, "chaves")));
if (OperatingSystem.IsWindows()) protecao.ProtectKeysWithDpapi(protectToLocalMachine: true);

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
    })
    // Login com Microsoft 365 (Entra ID). Tenant, aplicativo e chave vêm da tela
    // Configurações (ConfigurarMicrosoft365). Exige o portal em HTTPS (ou localhost).
    .AddOpenIdConnect(ConfigurarMicrosoft365.Esquema, "Microsoft 365", o =>
    {
        o.SignInScheme       = CookieAuthenticationDefaults.AuthenticationScheme;
        o.CallbackPath       = "/signin-oidc";
        o.ResponseType       = Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseType.Code;
        o.UsePkce            = true;
        o.SaveTokens         = false;
        o.MapInboundClaims   = false;
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("profile");
        o.Scope.Add("email");
        o.Events.OnTokenValidated = Microsoft365Login.AoValidarTokenAsync;
        o.Events.OnRemoteFailure  = Microsoft365Login.AoFalharAsync;
    });
builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>, ConfigurarMicrosoft365>();

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
builder.Services.AddScoped<IAtualizacaoAgenteService, AtualizacaoAgenteService>();
builder.Services.AddScoped<IConfiguracaoService, ConfiguracaoService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IRecuperacaoSenhaService, RecuperacaoSenhaService>();

// Pacotes do agente para a atualização automática: ao lado do banco (data\pacotes-agente),
// que o instalador do portal preserva nas atualizações.
builder.Services.AddSingleton(new PastaPacotesAgente(Path.Combine(pastaDados, "pacotes-agente")));

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
        if (resultado.Status == AuthStatus.SenhaExpirada && resultado.Usuario is { } vencida)
            return Results.Redirect($"/alterar-senha?obrigatoria=1&motivo=expirada&usuario={Uri.EscapeDataString(vencida.Username)}");
        if (resultado.Status == AuthStatus.UsarMicrosoft365)
            return Results.Redirect("/login?erro=usar-m365");
        if (resultado.Status != AuthStatus.Sucesso || resultado.Usuario is null)
            return Results.Redirect("/login?erro=1");

        var user = resultado.Usuario;
        var principal = SessaoPortal.Principal(user, "Local");

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        http.RequestServices.GetRequiredService<PresencaService>().Entrou(user.Username, user.DisplayName, ip);

        // Só aceita retorno para uma página do próprio portal
        if (!string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//"))
            return Results.Redirect(returnUrl);

        // Primeira página que o perfil pode abrir
        return Results.Redirect(await SessaoPortal.PaginaInicialAsync(http.RequestServices, principal));
    })
    .DisableAntiforgery()
    .RequireRateLimiting("login");

// GET /api/auth/m365 — botão "Entrar com Microsoft 365" da tela de login
app.MapGet("/api/auth/m365", async (HttpContext http, IConfiguracaoService config, ILogger<Program> log) =>
    {
        var m365 = await config.M365Async();
        if (!m365.LoginHabilitado || !m365.Configurado)
            return Results.Redirect("/login?erro=m365-desligado");
        try
        {
            // Busca a configuração do Tenant na Microsoft e redireciona para o login de lá
            await http.ChallengeAsync(ConfigurarMicrosoft365.Esquema, new AuthenticationProperties { RedirectUri = "/inicio" });
            return Results.Empty;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Não foi possível iniciar o login Microsoft 365 (Tenant {Tenant}).", m365.TenantId);
            return Results.Redirect("/login?erro=m365");
        }
    })
    .RequireRateLimiting("login");

// GET /inicio — depois do login Microsoft 365: primeira página que o perfil pode abrir
app.MapGet("/inicio", async (HttpContext http) =>
    http.User.Identity?.IsAuthenticated == true
        ? Results.Redirect(await SessaoPortal.PaginaInicialAsync(http.RequestServices, http.User))
        : Results.Redirect("/login"));

// POST /api/auth/recuperar-senha — "Esqueci minha senha": senha temporária por e-mail.
// A resposta é sempre a mesma, exista ou não a conta.
app.MapPost("/api/auth/recuperar-senha", async (
        HttpContext http,
        IRecuperacaoSenhaService recuperacao,
        [FromForm] string loginOuEmail) =>
    {
        var portal = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
        await recuperacao.SolicitarAsync(loginOuEmail, http.Connection.RemoteIpAddress?.ToString(), portal);
        return Results.Redirect("/recuperar-senha?enviado=1");
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
        [FromForm] string? senhaInicial,
        [FromForm] string? email,
        [FromForm] string? tipoAutenticacao,
        [FromForm] string? senhaExpira,
        [FromForm] string? exigirTroca) =>
    {
        var dados = new DadosUsuario(displayName, perfilId, email,
            tipoAutenticacao == "Microsoft365" ? TipoAutenticacao.Microsoft365 : TipoAutenticacao.Local,
            SenhaExpira: senhaExpira == "true", ExigirTroca: exigirTroca == "true");
        var resultado = await userService.CriarAsync(username, senhaInicial, dados);

        return resultado switch
        {
            UserOperationResult.Sucesso          => Results.Redirect("/usuarios?sucesso=criado"),
            UserOperationResult.UsuarioJaExiste  => Results.Redirect("/usuarios?erro=usuario-existe"),
            UserOperationResult.SenhaInvalida    => Results.Redirect("/usuarios?erro=senha-fraca"),
            UserOperationResult.EmailJaExiste    => Results.Redirect("/usuarios?erro=email-existe"),
            UserOperationResult.EmailObrigatorio => Results.Redirect("/usuarios?erro=email-m365"),
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
    // Mesma ordem escolhida na tela (clique no cabeçalho das colunas)
    return Ordenacao.DaUrl(q["ordenar"], "data").Aplicar(itens, AuditoriaOrdenacao.Acoes).ToList();
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
        linhasEventos = Ordenacao.DaUrl(req.Query["ordenar"], "data").Aplicar(linhasEventos, RelatorioOrdenacao.Colunas).ToList();

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
        IServerService serverService,
        IAtualizacaoAgenteService atualizacao) =>
    {
        if (string.IsNullOrWhiteSpace(dto.ServerName))
            return Results.BadRequest("ServerName é obrigatório.");

        // Só aceita servidores cadastrados e habilitados na tela Servidores.
        // A resposta leva a instrução de atualização quando o portal pediu uma.
        return await serverService.ProcessHeartbeatAsync(dto) switch
        {
            HeartbeatResult.Aceito        => Results.Ok(new
            {
                received = DateTime.UtcNow,
                update   = await atualizacao.InstrucaoParaAsync(dto.ServerName),
            }),
            HeartbeatResult.NaoCadastrado => Results.Problem(
                $"Servidor \"{dto.ServerName}\" não está cadastrado no portal.", statusCode: 403),
            _                             => Results.Problem(
                $"Servidor \"{dto.ServerName}\" está desabilitado no portal.", statusCode: 403),
        };
    });

// POST /api/agent/offline/{serverName}
// O agente avisa que está sendo encerrado (serviço parado, desinstalação,
// atualização): o servidor aparece offline na hora, sem esperar o tempo limite.
agentApi.MapPost("/offline/{serverName}", async (string serverName, AppDbContext db, ILogger<Program> log) =>
    {
        var nome = serverName.ToLower();
        var afetados = await db.Servers.Where(s => s.Name.ToLower() == nome && s.IsOnline)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsOnline, false));
        if (afetados > 0) log.LogInformation("Agente de {Server} avisou o encerramento: servidor offline.", serverName);
        return Results.Ok();
    });

// GET /api/agent/pacote/{versao}
// O agente baixa o pacote da versão nova (atualização automática).
agentApi.MapGet("/pacote/{versao}", async (string versao, IAtualizacaoAgenteService atualizacao) =>
    await atualizacao.ArquivoDoPacoteAsync(versao) is { } arquivo
        ? Results.File(arquivo.Caminho, "application/zip", arquivo.Nome)
        : Results.NotFound());

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
        [FromForm] string? email,
        [FromForm] string? tipoAutenticacao,
        [FromForm] string? senhaExpira,
        [FromForm] string? exigirTroca) =>
    {
        var dados = new DadosUsuario(displayName, perfilId, email,
            tipoAutenticacao == "Microsoft365" ? TipoAutenticacao.Microsoft365 : TipoAutenticacao.Local,
            SenhaExpira: senhaExpira == "true", ExigirTroca: exigirTroca == "true");
        var resultado = await userService.EditarAsync(id, novaSenha, dados);

        return resultado switch
        {
            UserOperationResult.Sucesso             => Results.Redirect("/usuarios?sucesso=editado"),
            UserOperationResult.SenhaInvalida       => Results.Redirect($"/usuarios/editar/{id}?erro=senha-fraca"),
            UserOperationResult.NaoEncontrado       => Results.Redirect("/usuarios?erro=nao-encontrado"),
            UserOperationResult.UltimoAdministrador => Results.Redirect($"/usuarios/editar/{id}?erro=ultimo-admin"),
            UserOperationResult.EmailJaExiste       => Results.Redirect($"/usuarios/editar/{id}?erro=email-existe"),
            UserOperationResult.EmailObrigatorio    => Results.Redirect($"/usuarios/editar/{id}?erro=email-m365"),
            _                                       => Results.Redirect($"/usuarios/editar/{id}?erro=dados-invalidos"),
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
