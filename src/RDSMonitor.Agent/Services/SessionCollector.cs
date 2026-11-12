using System.Diagnostics;
using System.Text.RegularExpressions;
using RDSMonitor.Agent.Models;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// Coleta sessões RDS ativas usando o comando nativo <c>quser</c>.
/// </summary>
public class SessionCollector
{
    private readonly ILogger<SessionCollector> _log;

    public SessionCollector(ILogger<SessionCollector> log) => _log = log;

    public List<SessionInfo> Collect()
    {
        // CPU e memória por sessão são somadas no Worker a partir dos processos.
        try
        {
            return RunQuser();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Erro ao coletar sessões.");
            return new();
        }
    }

    // ── quser ────────────────────────────────────────────────────────────────

    private List<SessionInfo> RunQuser()
    {
        var psi = new ProcessStartInfo("quser")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10_000);

        return ParseQuser(output);
    }

    /// <summary>
    /// Parseia a saída do quser linha a linha. O texto de STATE e IDLE TIME
    /// segue o idioma do Windows, então aceitamos inglês e português:
    /// <code>
    ///  USERNAME       SESSIONNAME    ID  STATE   IDLE TIME  LOGON TIME
    ///  joao.silva     rdp-tcp#15     15  Active         .   09/08/2026 08:00
    /// >carlos.lima    console         1  Ativo     nenhum   09/08/2026 09:00
    ///  ana.pereira                   31  Disc        1:00   09/08/2026 06:00
    ///  pedro.souza                   32  Desc    1+02:15    08/08/2026 17:00
    /// </code>
    /// </summary>
    internal static List<SessionInfo> ParseQuser(string output)
    {
        var result = new List<SessionInfo>();
        var lines  = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // ">" marca a sessão de quem executou o quser. SESSIONNAME fica vazio
        // em sessões desconectadas, por isso é opcional; o ID numérico serve
        // de âncora. O cabeçalho não casa porque não tem ID numérico.
        var re = new Regex(
            @"^[> ]?(?<u>\S+)\s+(?:(?<sn>\S+)\s+)?(?<id>\d+)\s+(?<st>\S+)\s+(?<idle>\S+)\s+(?<lt>\d.*)$");

        foreach (var rawLine in lines)
        {
            var m = re.Match(rawLine.TrimEnd());
            if (!m.Success) continue;

            var state = NormalizeState(m.Groups["st"].Value);
            if (state is null) continue;   // Listen/Escuta e estados desconhecidos

            var session = new SessionInfo
            {
                SessionId       = int.Parse(m.Groups["id"].Value),
                Username        = m.Groups["u"].Value.Trim(),
                SessionName     = m.Groups["sn"].Success ? m.Groups["sn"].Value : null,
                State           = state,
                LogonTime       = ParseLogonTime(m.Groups["lt"].Value.Trim()),
                IdleTimeMinutes = ParseIdle(m.Groups["idle"].Value.Trim()),
            };

            // Remove domínio se houver (DOMINIO\usuario → usuario)
            if (session.Username.Contains('\\'))
                session.Username = session.Username.Split('\\').Last();

            result.Add(session);
        }

        return result;
    }

    /// <summary>Estado em inglês ou português → valor da API; null = ignorar.</summary>
    private static string? NormalizeState(string raw) => raw.ToLowerInvariant() switch
    {
        "active" or "ativo" or "ativa"                         => "Active",
        "disc" or "desc" or "disconnected" or "desconectado"   => "Disconnected",
        "idle" or "ocioso"                                     => "Idle",
        _                                                      => null,
    };

    /// <summary>
    /// Ociosidade do quser: "." / "none" / "nenhum" = 0; "45" = minutos;
    /// "1:30" = horas:minutos; "2+03:15" = dias+horas:minutos.
    /// </summary>
    internal static int ParseIdle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;

        var dias = 0;
        var plus = raw.IndexOf('+');
        if (plus > 0)
        {
            int.TryParse(raw[..plus], out dias);
            raw = raw[(plus + 1)..];
        }

        int minutos;
        if (raw.Contains(':'))
        {
            var parts = raw.Split(':');
            minutos = (int.TryParse(parts[0], out var h) ? h : 0) * 60
                    + (int.TryParse(parts[1], out var mi) ? mi : 0);
        }
        else
        {
            minutos = int.TryParse(raw, out var mi) ? mi : 0;   // ".", "none", "nenhum" → 0
        }

        return dias * 24 * 60 + minutos;
    }

    /// <summary>
    /// O quser mostra a hora local do servidor. Convertemos para UTC para que
    /// o JSON saia com sufixo "Z" e o portal grave/exiba sem deslocamento.
    /// </summary>
    private static DateTime ParseLogonTime(string raw)
    {
        // Windows usa o locale da máquina — tentamos formatos comuns PT-BR e EN
        string[] formats =
        {
            "dd/MM/yyyy HH:mm", "M/d/yyyy h:mm tt", "d/M/yyyy H:mm",
            "MM/dd/yyyy hh:mm tt", "dd/MM/yyyy H:mm",
        };
        const System.Globalization.DateTimeStyles estilo =
            System.Globalization.DateTimeStyles.AssumeLocal |
            System.Globalization.DateTimeStyles.AdjustToUniversal;

        if (DateTime.TryParseExact(raw, formats, null, estilo, out var dt))
            return dt;

        return DateTime.TryParse(raw, null, estilo, out var dt2) ? dt2 : DateTime.UtcNow;
    }

}
