using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;

namespace RDSMonitor.Web.Services;

/// <summary>
/// Serviço em segundo plano que marca como offline os servidores sem contato do
/// agente há mais de <see cref="LimiteSemContato"/>. O contato é o heartbeat
/// (30 s) ou a busca de comandos (3 s), então um agente parado ou desinstalado
/// aparece offline em até ~25 s. Quando o serviço do agente é parado de forma
/// normal, ele mesmo avisa o portal (POST /api/agent/offline) e o efeito é imediato.
/// </summary>
public class ServerHealthMonitor : BackgroundService
{
    private static readonly TimeSpan Intervalo        = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LimiteSemContato = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory          _scopeFactory;
    private readonly ILogger<ServerHealthMonitor>  _log;

    public ServerHealthMonitor(IServiceScopeFactory scopeFactory, ILogger<ServerHealthMonitor> log)
    {
        _scopeFactory = scopeFactory;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Intervalo);

        try
        {
            do
            {
                try
                {
                    await MarcarOfflineAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Erro ao verificar servidores sem heartbeat.");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Encerramento normal do portal — não é erro.
        }
    }

    private async Task MarcarOfflineAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var limite = DateTime.UtcNow - LimiteSemContato;

        var afetados = await db.Servers
            .Where(s => s.IsOnline && (s.UltimoContato ?? s.LastHeartbeat ?? DateTime.MinValue) < limite)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsOnline, false), ct);

        if (afetados > 0)
            _log.LogWarning("{Qtd} servidor(es) marcado(s) como offline (sem contato do agente há mais de {Seg} s).",
                afetados, LimiteSemContato.TotalSeconds);
    }
}
