using RDSMonitor.Agent.Models;
using RDSMonitor.Agent.Services;
using RDSMonitor.Agent.Settings;

namespace RDSMonitor.Agent;

/// <summary>
/// Serviço de background principal do agente.
/// Roda dois loops independentes em paralelo:
///   1. Heartbeat — coleta e envia estado do servidor a cada N segundos.
///   2. Command Poll — busca e executa comandos pendentes a cada M segundos.
/// </summary>
public class Worker : BackgroundService
{
    private readonly AgentSettings       _settings;
    private readonly SessionCollector    _sessions;
    private readonly SessionEventCollector _eventos;
    private readonly ProcessCollector    _processes;
    private readonly ApiClient           _api;
    private readonly CommandExecutor     _executor;
    private readonly SimulatedSessionStore _simulacao;
    private readonly ILogger<Worker>     _log;

    // Evita que o heartbeat periódico e o heartbeat imediato (pós-comando)
    // sejam montados/enviados ao mesmo tempo.
    private readonly SemaphoreSlim _heartbeatGate = new(1, 1);

    public Worker(
        AgentSettings    settings,
        SessionCollector sessions,
        SessionEventCollector eventos,
        ProcessCollector processes,
        ApiClient        api,
        CommandExecutor  executor,
        SimulatedSessionStore simulacao,
        ILogger<Worker>  log)
    {
        _simulacao = simulacao;
        _settings  = settings;
        _sessions  = sessions;
        _eventos   = eventos;
        _processes = processes;
        _api       = api;
        _executor  = executor;
        _log       = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("RDSMonitor.Agent iniciado — Servidor: {Server} | API: {Url}",
            _settings.ServerName, _settings.WebApiUrl);

        // Roda os dois loops em paralelo; ambos param quando o serviço for encerrado
        await Task.WhenAll(
            HeartbeatLoopAsync(ct),
            CommandPollLoopAsync(ct));
    }

    // ── Loop de Heartbeat ─────────────────────────────────────────────────────

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        // Primeira execução imediata ao iniciar
        await SendHeartbeatAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_settings.HeartbeatIntervalSeconds), ct);
            await SendHeartbeatAsync(ct);
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        await _heartbeatGate.WaitAsync(ct);
        try
        {
            var (usedGb, totalGb) = ProcessCollector.GetMemoryInfo();
            var cpuPercent        = ProcessCollector.GetTotalCpuPercent();

            var sessions  = _sessions.Collect();
            var processes = _processes.Collect();
            var events    = _eventos.Coletar();

            // Modo de simulação: acrescenta as sessões, processos e eventos fictícios
            var eventosSimulados = new List<SessionEventInfo>();
            if (_simulacao.Habilitada)
            {
                sessions.AddRange(_simulacao.GetSessions());
                processes.AddRange(_simulacao.GetProcesses());
                eventosSimulados = _simulacao.GetEvents();
                events.AddRange(eventosSimulados);
            }

            // CPU e memória de cada sessão = soma dos processos dela
            var porSessao = processes
                .GroupBy(p => p.RdsSessionId)
                .ToDictionary(g => g.Key, g => (Cpu: g.Sum(p => p.CpuPercent), Mem: g.Sum(p => p.MemoryMb)));

            foreach (var s in sessions)
            {
                if (porSessao.TryGetValue(s.SessionId, out var tot))
                {
                    s.CpuPercent = Math.Round(Math.Min(tot.Cpu, 100), 1);
                    s.MemoryMb   = Math.Round(tot.Mem, 1);
                }
            }

            var heartbeat = new AgentHeartbeat
            {
                ServerName    = _settings.ServerName,
                Timestamp     = DateTime.UtcNow,
                CpuPercent    = cpuPercent,
                MemoryUsedGb  = usedGb,
                MemoryTotalGb = totalGb,
                Sessions      = sessions,
                Processes     = processes,
                Events        = events,
            };

            // Eventos só saem da fila quando o portal aceita o heartbeat
            if (await _api.SendHeartbeatAsync(heartbeat, ct))
            {
                _eventos.Confirmar(events);
                if (eventosSimulados.Count > 0) _simulacao.ConfirmarEventos(eventosSimulados);
            }
        }
        catch (OperationCanceledException) { /* serviço encerrando */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "Erro no loop de heartbeat.");
        }
        finally
        {
            _heartbeatGate.Release();
        }
    }

    // ── Loop de Comandos ──────────────────────────────────────────────────────

    private async Task CommandPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_settings.CommandPollIntervalSeconds), ct);
            await ProcessPendingCommandsAsync(ct);
        }
    }

    private async Task ProcessPendingCommandsAsync(CancellationToken ct)
    {
        try
        {
            var commands = await _api.GetPendingCommandsAsync(ct);

            var executou = false;
            foreach (var cmd in commands)
            {
                if (ct.IsCancellationRequested) break;

                var result = await _executor.ExecuteAsync(cmd);
                await _api.ReportCommandResultAsync(cmd.Id, result, ct);
                executou = true;
            }

            // Reflete o resultado (sessão encerrada, processo derrubado, etc.) na
            // tela imediatamente, sem aguardar o próximo heartbeat periódico.
            if (executou && !ct.IsCancellationRequested)
                await SendHeartbeatAsync(ct);
        }
        catch (OperationCanceledException) { /* serviço encerrando */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "Erro no loop de comandos.");
        }
    }
}
