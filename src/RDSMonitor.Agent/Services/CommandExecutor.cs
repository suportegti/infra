using System.Diagnostics;
using System.Management;
using RDSMonitor.Agent.Models;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// Executa os comandos recebidos da API central usando ferramentas nativas do Windows:
/// logoff, tsdiscon, Process.Kill, WTSSendMessage, ServiceController e shutdown.
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
            _log.LogError(ex, "Exceção ao executar comando {Type} (Id:{Id}).", command.Type, command.Id);
            return new CommandResult { Success = false, Error = $"{ex.Message} (detalhes no log local do agente: logs\\agente-{DateTime.Now:yyyyMMdd}.log)" };
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

    /// <summary>
    /// Mostra uma janela de mensagem na sessão do usuário via WTSSendMessage
    /// (o msg.exe dependia de aspas e do idioma e falhava a partir do serviço).
    /// </summary>
    private Task<CommandResult> SendMessageAsync(Dictionary<string, string> p)
    {
        if (!p.TryGetValue("SessionId", out var sessaoTexto) || !int.TryParse(sessaoTexto, out var sessao) || sessao < 0)
            return Task.FromResult(Erro("SessionId inválido ou não informado."));

        p.TryGetValue("Message", out var mensagem);
        p.TryGetValue("Title", out var titulo);
        if (string.IsNullOrWhiteSpace(mensagem))
            return Task.FromResult(Erro("Mensagem vazia."));

        titulo = string.IsNullOrWhiteSpace(titulo) ? "Mensagem do administrador" : titulo.Trim();
        var timeout = p.TryGetValue("TimeoutSeconds", out var t) && int.TryParse(t, out var seg) && seg > 0 ? seg : 300;

        try
        {
            WtsSessionApi.EnviarMensagem(sessao, titulo, mensagem.Trim(), timeout);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return Task.FromResult(Erro($"Não foi possível enviar a mensagem para a sessão {sessao}: {ex.Message} (código {ex.NativeErrorCode})."));
        }

        _log.LogInformation("Mensagem enviada para a sessão {Sessao}.", sessao);
        return Task.FromResult(new CommandResult { Success = true, Output = $"Mensagem exibida na sessão {sessao}." });
    }

    // ── Restart Service ───────────────────────────────────────────────────────

    /// <summary>
    /// Reinicia um serviço do Windows (padrão: TermService). Os serviços que
    /// dependem dele (ex.: UmRdpService) são parados antes e religados depois —
    /// o "sc stop" falhava com o erro 1051 justamente por causa deles.
    /// ATENÇÃO: reiniciar o TermService desconecta todas as sessões RDP.
    /// </summary>
    private Task<CommandResult> RestartServiceAsync(Dictionary<string, string> p) => Task.Run(() =>
    {
        p.TryGetValue("ServiceName", out var nome);
        nome = string.IsNullOrWhiteSpace(nome) ? "TermService" : nome.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(nome, @"^[A-Za-z0-9_.\-]{1,80}$"))
            return Erro($"Nome de serviço inválido: {nome}");

        using var servico = new System.ServiceProcess.ServiceController(nome);
        try { _ = servico.Status; }
        catch (InvalidOperationException) { return Erro($"O serviço \"{nome}\" não existe neste servidor."); }

        var limite = TimeSpan.FromSeconds(90);
        var diagnostico = $"status={servico.Status}, aceita parar={(servico.CanStop ? "sim" : "não")}, tipo={servico.ServiceType}";
        _log.LogInformation("Reiniciar {Servico}: {Diagnostico}", nome, diagnostico);

        // Em servidores com a função Host de Sessão da Área de Trabalho Remota o Windows
        // não deixa parar o TermService (o serviço informa que não aceita STOP).
        if (servico.Status == System.ServiceProcess.ServiceControllerStatus.Running && !servico.CanStop)
        {
            _log.LogWarning("O serviço {Servico} não aceita o comando de parar ({Diagnostico}).", nome, diagnostico);
            return Erro($"O Windows não permite parar o serviço \"{nome}\" neste servidor (o serviço não aceita o comando STOP). " +
                        (nome.Equals("TermService", StringComparison.OrdinalIgnoreCase)
                            ? "Isso é normal em servidores com a função Host de Sessão da Área de Trabalho Remota: o TermService não pode ser reiniciado com o servidor em funcionamento. Use \"Reiniciar servidor\"."
                            : "Use \"Reiniciar servidor\"."));
        }

        // Dependentes em execução (recursivo), na ordem pai → filho
        var dependentes = new List<string>();
        void Coletar(System.ServiceProcess.ServiceController sc)
        {
            foreach (var d in sc.DependentServices)
            {
                using (d)
                {
                    if (d.Status != System.ServiceProcess.ServiceControllerStatus.Stopped
                        && !dependentes.Contains(d.ServiceName, StringComparer.OrdinalIgnoreCase))
                    {
                        dependentes.Add(d.ServiceName);
                        Coletar(d);
                    }
                }
            }
        }
        Coletar(servico);

        try
        {
            if (servico.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                _log.LogInformation("Parando {Servico} (dependentes: {Deps})...", nome,
                    dependentes.Count == 0 ? "nenhum" : string.Join(", ", dependentes));
                servico.Stop();   // também para os serviços dependentes
                servico.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, limite);
            }

            servico.Start();
            servico.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, limite);

            var falharam = new List<string>();
            foreach (var nomeDep in dependentes)
            {
                try
                {
                    using var dep = new System.ServiceProcess.ServiceController(nomeDep);
                    if (dep.Status == System.ServiceProcess.ServiceControllerStatus.Stopped) dep.Start();
                    dep.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Dependente {Dep} não voltou a rodar.", nomeDep);
                    falharam.Add(nomeDep);
                }
            }

            var deps = dependentes.Count == 0 ? "" : $" Dependentes religados: {string.Join(", ", dependentes.Except(falharam))}.";
            if (falharam.Count > 0)
                return new CommandResult
                {
                    Success = false,
                    Output  = $"Serviço \"{nome}\" reiniciado.{deps}",
                    Error   = $"Serviço \"{nome}\" reiniciado, mas não voltaram a rodar: {string.Join(", ", falharam)}.",
                };

            _log.LogInformation("Serviço {Servico} reiniciado.", nome);
            return new CommandResult { Success = true, Output = $"Serviço \"{nome}\" reiniciado.{deps}" };
        }
        catch (System.ServiceProcess.TimeoutException ex)
        {
            servico.Refresh();
            _log.LogError(ex, "Tempo esgotado reiniciando {Servico} ({Diagnostico}).", nome, diagnostico);
            return Erro($"O serviço \"{nome}\" não mudou de estado em {limite.TotalSeconds:0} segundos (status atual: {servico.Status}).");
        }
        catch (InvalidOperationException ex)
        {
            // O motivo real vem do Windows (Win32Exception) dentro da exceção
            var win32 = ex.InnerException as System.ComponentModel.Win32Exception;
            var motivo = win32?.NativeErrorCode switch
            {
                5    => "acesso negado — o agente precisa rodar como LocalSystem ou administrador",
                1051 => "há serviços dependentes em execução que não puderam ser parados",
                1052 or 1061 => "o serviço não aceita o comando de parar neste momento (comum no TermService de servidores de sessão RDS)",
                1053 => "o serviço não respondeu a tempo",
                _    => null,
            };
            _log.LogError(ex, "Falha ao reiniciar {Servico} ({Diagnostico}); código Windows {Codigo}.", nome, diagnostico, win32?.NativeErrorCode);
            return Erro($"Falha ao reiniciar \"{nome}\": {win32?.Message ?? ex.Message}" +
                        (win32 is not null ? $" (código {win32.NativeErrorCode}{(motivo is null ? "" : $": {motivo}")})" : "") +
                        $". Detalhes no log do agente: logs\\agente-{DateTime.Now:yyyyMMdd}.log");
        }
    });

    // ── Restart Server ────────────────────────────────────────────────────────

    private async Task<CommandResult> RestartServerAsync(Dictionary<string, string> p)
    {
        var delay = p.TryGetValue("DelaySeconds", out var d) && int.TryParse(d, out var s) ? Math.Clamp(s, 0, 600) : 30;

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
