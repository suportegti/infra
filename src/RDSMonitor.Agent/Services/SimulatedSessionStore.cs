using RDSMonitor.Agent.Models;
using RDSMonitor.Agent.Settings;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// Sessões e processos FICTÍCIOS do modo de simulação (ver
/// <see cref="SimulationSettings"/>). Mantém o estado em memória e aplica os
/// comandos do portal sobre ele, para que Disconnect / Logoff / Derrubar
/// possam ser testados de ponta a ponta numa máquina de desenvolvimento.
///
/// Convenções para não colidir com o Windows real:
///  • IDs de sessão a partir de 901, nome de sessão "sim#ID";
///  • PIDs a partir de 9.000.000.
/// Acessado pelos dois loops do Worker (heartbeat e comandos) — tudo sob lock.
/// </summary>
public class SimulatedSessionStore
{
    public const string PrefixoSessao = "sim#";
    private const string Saida        = "[SIMULAÇÃO] ";

    private readonly object _lock = new();
    private readonly SimulationSettings _cfg;
    private readonly ILogger<SimulatedSessionStore> _log;
    private readonly Random _rnd = new();

    private readonly List<SimSession> _sessoes = new();
    private readonly List<(string Usuario, DateTime Em)> _relogins = new();
    private int _proximaSessao = 901;
    private int _proximoPid    = 9_000_000;

    public bool Habilitada => _cfg.Habilitada;

    public SimulatedSessionStore(AgentSettings settings, ILogger<SimulatedSessionStore> log)
    {
        _cfg = settings.Simulacao;
        _log = log;
        if (!_cfg.Habilitada) return;

        var usuarios = _cfg.Usuarios.Count > 0 ? _cfg.Usuarios : new List<string> { "ana.teste", "bruno.teste", "carla.teste" };
        foreach (var u in usuarios) _sessoes.Add(NovaSessao(u, DateTime.UtcNow.AddMinutes(-_rnd.Next(10, 240))));

        _log.LogWarning("MODO DE SIMULAÇÃO ATIVO: {Qtd} sessão(ões) fictícia(s) ({Usuarios}). " +
                        "Logoff/Disconnect/reinício em sessões reais estão bloqueados.",
            _sessoes.Count, string.Join(", ", usuarios));
    }

    // ── Leitura (heartbeat) ───────────────────────────────────

    public List<SessionInfo> GetSessions()
    {
        lock (_lock)
        {
            ProcessarRelogins();
            var agora = DateTime.UtcNow;
            return _sessoes.Select(s => new SessionInfo
            {
                SessionId       = s.Id,
                Username        = s.Usuario,
                SessionName     = PrefixoSessao + s.Id,
                State           = s.DesconectadaEm is null ? "Active" : "Disconnected",
                LogonTime       = s.LogonUtc,
                IdleTimeMinutes = s.DesconectadaEm is { } d ? (int)(agora - d).TotalMinutes : 0,
            }).ToList();
        }
    }

    public List<ProcessInfo> GetProcesses()
    {
        lock (_lock)
        {
            return _sessoes.SelectMany(s => s.Processos.Select(p => new ProcessInfo
            {
                Pid          = p.Pid,
                Name         = p.Nome,
                Username     = p.Protegido ? null : s.Usuario,
                RdsSessionId = s.Id,
                // Sessão desconectada: programas continuam rodando, mas quase sem CPU
                CpuPercent   = Math.Round(p.CpuBase * (s.DesconectadaEm is null ? _rnd.NextDouble() * 2 : 0.1), 1),
                MemoryMb     = Math.Round(p.MemoriaMb * (0.95 + _rnd.NextDouble() * 0.1), 1),
                IsProtected  = p.Protegido,
            })).ToList();
        }
    }

    // ── Comandos ──────────────────────────────────────────────

    public bool PossuiSessao(int sessionId) { lock (_lock) return _sessoes.Any(s => s.Id == sessionId); }
    public bool PossuiPid(int pid)          { lock (_lock) return _sessoes.Any(s => s.Processos.Any(p => p.Pid == pid)); }

    /// <summary>Sessão fictícia já encerrada (ID na faixa da simulação, mas não existe mais).</summary>
    public bool EhIdSimulado(int sessionId) => _cfg.Habilitada && sessionId >= 901 && sessionId < 2000;
    public bool EhPidSimulado(int pid)      => _cfg.Habilitada && pid >= 9_000_000;

    public CommandResult Disconnect(int sessionId)
    {
        lock (_lock)
        {
            var s = _sessoes.FirstOrDefault(x => x.Id == sessionId);
            if (s is null) return Erro($"Sessão simulada {sessionId} não existe mais.");
            if (s.DesconectadaEm is not null) return Erro($"A sessão {sessionId} de {s.Usuario} já está desconectada.");

            s.DesconectadaEm = DateTime.UtcNow;
            _log.LogInformation("[SIMULAÇÃO] tsdiscon {Id} — {Usuario} desconectado.", sessionId, s.Usuario);
            return Ok($"Sessão {sessionId} de {s.Usuario} desconectada (tsdiscon {sessionId}). Os programas continuam abertos.");
        }
    }

    public CommandResult Logoff(int sessionId)
    {
        lock (_lock)
        {
            var s = _sessoes.FirstOrDefault(x => x.Id == sessionId);
            if (s is null) return Erro($"Sessão simulada {sessionId} não existe mais.");

            _sessoes.Remove(s);
            _relogins.Add((s.Usuario, DateTime.UtcNow.AddSeconds(_cfg.RelogarAposSegundos)));
            _log.LogInformation("[SIMULAÇÃO] logoff {Id} — sessão de {Usuario} encerrada ({Procs} processos fechados).",
                sessionId, s.Usuario, s.Processos.Count);
            return Ok($"Logoff da sessão {sessionId} de {s.Usuario} concluído (logoff {sessionId}); " +
                      $"{s.Processos.Count} processo(s) encerrados. O usuário fictício fará login de novo em {_cfg.RelogarAposSegundos}s.");
        }
    }

    public CommandResult Kill(int pid, string? nomeEsperado)
    {
        lock (_lock)
        {
            foreach (var s in _sessoes)
            {
                var p = s.Processos.FirstOrDefault(x => x.Pid == pid);
                if (p is null) continue;

                if (!string.IsNullOrWhiteSpace(nomeEsperado) && !string.Equals(p.Nome, nomeEsperado, StringComparison.OrdinalIgnoreCase))
                    return Erro($"O PID {pid} agora pertence a \"{p.Nome}\", não a \"{nomeEsperado}\". Nada foi encerrado.");
                if (p.Protegido)
                    return Erro($"\"{p.Nome}\" (PID {pid}) é um processo protegido do sistema e não pode ser derrubado.");

                s.Processos.Remove(p);
                _log.LogInformation("[SIMULAÇÃO] Processo {Nome} (PID {Pid}) de {Usuario} encerrado.", p.Nome, pid, s.Usuario);
                return Ok($"Processo \"{p.Nome}\" (PID {pid}) de {s.Usuario} encerrado.");
            }
            return Erro($"O processo {pid} não está mais em execução.");
        }
    }

    public CommandResult SendMessage(int sessionId, string? mensagem)
    {
        lock (_lock)
        {
            var s = _sessoes.FirstOrDefault(x => x.Id == sessionId);
            if (s is null) return Erro($"Sessão simulada {sessionId} não existe mais.");
            _log.LogInformation("[SIMULAÇÃO] msg {Id} para {Usuario}: {Msg}", sessionId, s.Usuario, mensagem);
            return Ok($"Mensagem exibida para {s.Usuario} (sessão {sessionId}): \"{mensagem}\"");
        }
    }

    // ── Internos ──────────────────────────────────────────────

    private void ProcessarRelogins()
    {
        var agora = DateTime.UtcNow;
        foreach (var r in _relogins.Where(r => r.Em <= agora).ToList())
        {
            _relogins.Remove(r);
            var s = NovaSessao(r.Usuario, agora);
            _sessoes.Add(s);
            _log.LogInformation("[SIMULAÇÃO] {Usuario} fez login de novo (sessão {Id}).", r.Usuario, s.Id);
        }
    }

    private SimSession NovaSessao(string usuario, DateTime logonUtc)
    {
        var s = new SimSession { Id = _proximaSessao++, Usuario = usuario, LogonUtc = logonUtc };

        // Processos de sistema da sessão (protegidos) + programas do usuário
        s.Processos.Add(Proc("csrss",       2,   0.1, protegido: true));
        s.Processos.Add(Proc("winlogon",    4,   0.0, protegido: true));
        s.Processos.Add(Proc("dwm",         60,  0.8, protegido: true));
        s.Processos.Add(Proc("explorer",    90,  0.6));
        s.Processos.Add(Proc("rdpclip",     8,   0.1));
        s.Processos.Add(Proc("chrome",      420, 3.0));
        s.Processos.Add(Proc("chrome",      180, 1.2));
        s.Processos.Add(Proc("chrome",      95,  0.4));

        var apps = new[] { ("WINWORD", 160.0, 1.5), ("EXCEL", 210.0, 2.5), ("OUTLOOK", 240.0, 1.0), ("Teams", 380.0, 2.0), ("AcroRd32", 120.0, 0.5) };
        foreach (var (nome, mem, cpu) in apps.OrderBy(_ => _rnd.Next()).Take(_rnd.Next(2, 4)))
            s.Processos.Add(Proc(nome, mem, cpu));

        return s;
    }

    private SimProcess Proc(string nome, double memMb, double cpu, bool protegido = false)
        => new() { Pid = _proximoPid += 4, Nome = nome, MemoriaMb = memMb, CpuBase = cpu, Protegido = protegido };

    private static CommandResult Ok(string msg)   => new() { Success = true,  Output = Saida + msg };
    private static CommandResult Erro(string msg) => new() { Success = false, Error  = Saida + msg };

    private sealed class SimSession
    {
        public int       Id;
        public string    Usuario = string.Empty;
        public DateTime  LogonUtc;
        public DateTime? DesconectadaEm;
        public List<SimProcess> Processos { get; } = new();
    }

    private sealed class SimProcess
    {
        public int    Pid;
        public string Nome = string.Empty;
        public double MemoriaMb;
        public double CpuBase;
        public bool   Protegido;
    }
}
