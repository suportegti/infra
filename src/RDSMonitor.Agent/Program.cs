using RDSMonitor.Agent;
using RDSMonitor.Agent.Services;
using RDSMonitor.Agent.Settings;

// Diagnóstico: lista as sessões vistas pelo agente e sai (não sobe o serviço).
if (args.Any(a => a.Equals("--diagnostico-sessoes", StringComparison.OrdinalIgnoreCase)))
    return DiagnosticoSessoes.Executar();

var builder = Host.CreateApplicationBuilder(args);

// ── Windows Service ───────────────────────────────────────────────────────────
builder.Services.AddWindowsService(o => o.ServiceName = "RDSMonitor.Agent");

// ── Configuração ──────────────────────────────────────────────────────────────
var settings = builder.Configuration
    .GetSection("AgentSettings")
    .Get<AgentSettings>() ?? new AgentSettings();

if (string.IsNullOrWhiteSpace(settings.ServerName))
    throw new InvalidOperationException(
        "AgentSettings:ServerName não configurado em appsettings.json. " +
        "Informe o nome deste servidor (ex: \"RDS01\").");

if (string.IsNullOrWhiteSpace(settings.WebApiUrl))
    throw new InvalidOperationException(
        "AgentSettings:WebApiUrl não configurado em appsettings.json. " +
        "Informe a URL do WebServer (ex: \"https://rdsmonitor.empresa.local\").");

if (string.IsNullOrWhiteSpace(settings.ApiKey))
    throw new InvalidOperationException(
        "AgentSettings:ApiKey não configurado. " +
        "Use o mesmo valor de AgentSettings:ApiKey do appsettings.json do WebServer.");

// Trava de segurança: o modo de simulação só existe em Development. Num
// servidor RDS (serviço do Windows = ambiente Production) ele é desligado
// mesmo que alguém o habilite no appsettings.json.
if (settings.Simulacao.Habilitada && !builder.Environment.IsDevelopment())
{
    settings.Simulacao.Habilitada = false;
    Console.Error.WriteLine("AVISO: AgentSettings:Simulacao:Habilitada ignorado fora do ambiente Development.");
}

builder.Services.AddSingleton(settings);

// ── HttpClient ────────────────────────────────────────────────────────────────
// Registrado como singleton diretamente — sem IHttpClientFactory nem
// Microsoft.Extensions.Http. O agente só precisa de um cliente HTTP.
builder.Services.AddSingleton(_ =>
{
    var handler = new HttpClientHandler();

    // Certificado auto-assinado no WebServer (somente ambientes de teste)
    if (settings.IgnorarErrosCertificado)
        handler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

    var client = new HttpClient(handler)
    {
        BaseAddress = new Uri(settings.WebApiUrl.TrimEnd('/') + "/"),
        Timeout     = TimeSpan.FromSeconds(30),
    };
    client.DefaultRequestHeaders.Add("X-Agent-Key", settings.ApiKey);
    return client;
});

// ── Serviços do agente ────────────────────────────────────────────────────────
builder.Services.AddSingleton<SessionCollector>();
builder.Services.AddSingleton<ProcessCollector>();
builder.Services.AddSingleton<ApiClient>();
builder.Services.AddSingleton<CommandExecutor>();
builder.Services.AddSingleton<SimulatedSessionStore>();   // inativo, a menos que AgentSettings:Simulacao:Habilitada

// ── Worker principal ──────────────────────────────────────────────────────────
builder.Services.AddHostedService<Worker>();

// ── Log no Windows Event Log (quando rodar como serviço) ─────────────────────
builder.Logging.AddEventLog(o =>
{
    o.SourceName = "RDSMonitor.Agent";
    o.LogName    = "Application";
});

var host = builder.Build();
host.Run();
return 0;
