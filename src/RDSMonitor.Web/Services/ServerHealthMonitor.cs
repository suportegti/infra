using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;

namespace RDSMonitor.Web.Services;

/// <summary>
/// Serviço em segundo plano que marca como offline os servidores cujo último
/// heartbeat é mais antigo que <see cref="LimiteSemHeartbeat"/>. O heartbeat
/// (ServerService.ProcessHeartbeatAsync) só liga o IsOnline; é aqui que ele
/// volta a ser desligado quando o agente para de reportar.
/// </summary>
public class ServerHealthMonitor : BackgroundService
{
    private static readonly TimeSpan Intervalo          = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LimiteSemHeartbeat = TimeSpan.FromMinutes(2);

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

        var limite = DateTime.UtcNow - LimiteSemHeartbeat;

        var afetados = await db.Servers
            .Where(s => s.IsOnline && (s.LastHeartbeat == null || s.LastHeartbeat < limite))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsOnline, false), ct);

        if (afetados > 0)
            _log.LogWarning("{Qtd} servidor(es) marcado(s) como offline (sem heartbeat há mais de {Min} min).",
                afetados, LimiteSemHeartbeat.TotalMinutes);
    }
}
