using System.Diagnostics;
using System.Management;
using RDSMonitor.Agent.Models;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// Executa os comandos recebidos da API central usando ferramentas nativas do Windows:
/// logoff, tsdiscon, taskkill, msg, sc e shutdown.
/// </summary>
public class CommandExecutor
{
    private readonly ILogger<CommandExecutor> _log;
    private readonly SimulatedSessionStore    _sim;

    public CommandExecutor(ILogger<CommandExecutor> log, SimulatedSessionStore sim)
    {
        _log = log;
        _sim = sim;
    }

    /// <summary>
    /// Despacha o comando para o método correto e retorna o resultado.
    /// </summary>
    public async Task<CommandResult> ExecuteAsync(CommandDto command)
    {
        _log.LogInformation("Executando comando {Type} (Id:{Id}) Params:{Params}",
            command.Type, command.Id,
            string.Join(", ", command.Parameters.Select(p => $"{p.Key}={p.Value}")));

        try
        {
            if (_sim.Habilitada && TratarSimulacao(command) is { } resultadoSimulado)
                return resultadoSimulado;

            return command.Type.ToLower() switch
            {
                "logoff"         => await LogoffAsync(command.Parameters),
                "disconnect"     => await DisconnectAsync(command.Parameters),
                "killprocess"    => await KillProcessAsync(command.Parameters),
                "sendmessage"    => await SendMessageAsync(command.Parameters),
                "restartservice" => await RestartServiceAsync(command.Parameters),
                "restartserver"  => await RestartServerAsync(command.Parameters),
                _                => new CommandResult { Success = false, Error = $"Tipo desconhecido: {command.Type}" }
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Exceção ao executar comando {Type}.", command.Type);
            return new CommandResult { Success = false, Error = ex.Message };
        }
    }

    // ── Modo de simulação ─────────────────────────────────────────────────────

    /// <summary>
    /// Com a simulação ligada: comandos para sessões/PIDs fictícios são
    /// aplicados no <see cref="SimulatedSessionStore"/>; Logoff, Disconnect e
    /// reinícios nas sessões REAIS da máquina são recusados. Devolve null quando
    /// o comando deve seguir o fluxo normal (ex.: derrubar um processo real).
    /// </summary>
    private CommandResult? TratarSimulacao(CommandDto command)
    {
        var p    = command.Parameters;
        var tipo = command.Type.ToLower();

        int? sessao = p.TryGetValue("SessionId", out var s) && int.TryParse(s, out var sid) ? sid : null;
        int? pid    = p.TryGetValue("Pid", out var x) && int.TryParse(x, out var px) ? px : null;

        // Alvo fictício (existente ou já encerrado)
        if (pid is { } alvoPid && (_sim.PossuiPid(alvoPid) || _sim.EhPidSimulado(alvoPid)))
            return tipo == "killprocess" ? _sim.Kill(alvoPid, p.GetValueOrDefault("ProcessName")) : null;

        if (sessao is { } alvo && (_sim.PossuiSessao(alvo) || _sim.EhIdSimulado(alvo)))
        {
            return tipo switch
            {
                "disconnect"  => _sim.Disconnect(alvo),
                "logoff"      => _sim.Logoff(alvo),
                "sendmessage" => _sim.SendMessage(alvo, p.GetValueOrDefault("Message")),
                _             => null,
            };
        }

        // Alvo real: bloqueia o que derrubaria a sessão de desenvolvimento ou a máquina
        if (tipo is "logoff" or "disconnect" or "restartservice" or "restartserver")
        {
            _log.LogWarning("[SIMULAÇÃO] Comando {Tipo} bloqueado para alvo real ({Params}).",
                command.Type, string.Join(", ", p.Select(kv => $"{kv.Key}={kv.Value}")));
            return Erro($"Bloqueado: o agente está em modo de simulação e não executa {command.Type} " +
                        "em sessões reais nem reinicia esta máquina. Use uma sessão marcada como \"simulada\".");
        }

        return null;
    }

    // ── Logoff ────────────────────────────────────────────────────────────────

    private async Task<CommandResult> LogoffAsync(Dictionary<string, string> p)
    {
        if (!p.TryGetValue("SessionId", out var sessionId))
            return Erro("SessionId não informado.");

        // logoff <ID> — encerra a sessão e salva o perfil do usuário
        return await RunCmdAsync("logoff", sessionId);
    }

    // ── Disconnect ────────────────────────────────────────────────────────────

    private async Task<CommandResult> DisconnectAsync(Dictionary<string, string> p)
    {
        if (!p.TryGetValue("SessionId", out var sessionId))
            return Erro("SessionId não informado.");

        // tsdiscon <ID> — desconecta sem encerrar a sessão
        return await RunCmdAsync("tsdiscon", sessionId);
    }

    // ── Kill Process ──────────────────────────────────────────────────────────

    /// <summary>
    /// Derruba um processo pelo PID. Antes de matar, confere se o processo
    /// ainda é o mesmo que o operador viu no portal (nome e sessão, quando
    /// informados — o Windows reutiliza PIDs) e se não é um processo protegido.
    /// </summary>
    private async Task<CommandResult> KillProcessAsync(Dictionary<string, string> p)
    {
        if (!p.TryGetValue("Pid", out var pidTexto) || !int.TryParse(pidTexto, out var pid) || pid <= 4)
            return Erro("PID inválido ou não informado.");

        Process proc;
        try
        {
            proc = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return Erro($"O processo {pid} não está mais em execução.");
        }

        using (proc)
        {
            var nome = proc.ProcessName;

            if (p.TryGetValue("ProcessName", out var nomeEsperado) && !string.IsNullOrWhiteSpace(nomeEsperado)
                && !string.Equals(nome, nomeEsperado, StringComparison.OrdinalIgnoreCase))
                return Erro($"O PID {pid} agora pertence a \"{nome}\", não a \"{nomeEsperado}\". Nada foi encerrado.");

            if (p.TryGetValue("SessionId", out var sessaoTexto) && int.TryParse(sessaoTexto, out var sessaoEsperada)
                && proc.SessionId != sessaoEsperada)
                return Erro($"O PID {pid} está na sessão {proc.SessionId}, não na {sessaoEsperada}. Nada foi encerrado.");

            var dono = ProcessCollector.GetOwnerByPid(pid);
            if (ProcessProtection.IsProtected(nome, dono?.Sid, proc.SessionId))
                return Erro($"\"{nome}\" (PID {pid}) é um processo protegido do sistema e não pode ser derrubado.");

            proc.Kill(entireProcessTree: false);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return Erro($"Sinal enviado, mas \"{nome}\" (PID {pid}) não encerrou em 10 segundos.");
            }

            _log.LogInformation("Processo {Nome} (PID {Pid}, sessão {Sessao}, dono {Dono}) encerrado.",
                nome, pid, proc.SessionId, dono?.Username);

            return new CommandResult
            {
                Success = true,
                Output  = $"Processo \"{nome}\" (PID {pid}) de {dono?.Username ?? "?"} encerrado.",
            };
        }
    }

    // ── Send Message ──────────────────────────────────────────────────────────

    private async Task<CommandResult> SendMessageAsync(Dictionary<string, string> p)
    {
        if (!p.TryGetValue("SessionId", out var sessionId))
            return Erro("SessionId não informado.");

        p.TryGetValue("Message", out var message);
        p.TryGetValue("Title", out var title);

        message ??= "Mensagem do administrador.";
        title   ??= "RDSManager";

        // msg <ID> /TIME:30 <MENSAGEM>
        var args = $"{sessionId} /TIME:30 \"{message}\" /V";
        return await RunCmdAsync("msg", args);
    }

    // ── Restart Service ───────────────────────────────────────────────────────

    private async Task<CommandResult> RestartServiceAsync(Dictionary<string, string> p)
    {
        p.TryGetValue("ServiceName", out var serviceName);
        serviceName ??= "TermService";

        var stop   = await RunCmdAsync("sc", $"stop {serviceName}");
        await Task.Delay(3_000); // aguarda parada
        var start  = await RunCmdAsync("sc", $"start {serviceName}");

        return new CommandResult
        {
            Success = start.Success,
            Output  = $"stop: {stop.Output} | start: {start.Output}",
            Error   = start.Success ? null : start.Error,
        };
    }

    // ── Restart Server ────────────────────────────────────────────────────────

    private async Task<CommandResult> RestartServerAsync(Dictionary<string, string> p)
    {
        p.TryGetValue("DelaySeconds", out var delay);
        delay ??= "30";

        // shutdown /r /t <segundos> /c "RDSManager: reinício solicitado pelo administrador"
        var args = $"/r /t {delay} /c \"RDSManager: reinicio solicitado pelo portal\"";
        return await RunCmdAsync("shutdown", args);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<CommandResult> RunCmdAsync(string exe, string args)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            }
        };

        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        var success = proc.ExitCode == 0;
        var output  = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

        _log.LogInformation("{Exe} {Args} → ExitCode={Code}", exe, args, proc.ExitCode);

        return new CommandResult
        {
            Success = success,
            Output  = success ? output.Trim() : null,
            Error   = success ? null : output.Trim(),
        };
    }

    private static CommandResult Erro(string msg)
        => new() { Success = false, Error = msg };
}
