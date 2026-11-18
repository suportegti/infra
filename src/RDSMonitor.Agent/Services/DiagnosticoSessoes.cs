using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// <c>RDSMonitor.Agent.exe --diagnostico-sessoes</c>: mostra no console as sessões
/// que o agente enxerga neste servidor e o que seria enviado ao portal, sem
/// precisar parar o serviço nem configurar nada. Útil para conferir sessões
/// desconectadas num servidor RDS real.
/// </summary>
internal static class DiagnosticoSessoes
{
    public static int Executar()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"RDSMonitor.Agent - diagnóstico de sessões em {Environment.MachineName} ({DateTime.Now:dd/MM/yyyy HH:mm:ss})");
        Console.WriteLine($"Executando como: {Environment.UserDomainName}\\{Environment.UserName}");
        Console.WriteLine();

        Console.WriteLine("1) API WTS (fonte principal do agente)");
        try
        {
            var wts = WtsSessionApi.ListarSessoesComUsuario();
            if (wts.Count == 0) Console.WriteLine("   nenhuma sessão com usuário");
            foreach (var s in wts)
                Console.WriteLine($"   ID {s.SessionId,-4} {Usuario(s.Dominio, s.Usuario),-30} estado={s.Estado,-13} " +
                                  $"estação={s.NomeEstacao ?? "(vazio)",-14} logon={s.LogonUtc?.ToLocalTime().ToString("dd/MM HH:mm") ?? "?"} ocioso={s.OciosidadeMinutos} min " +
                                  $"origem={s.ClienteNome ?? "-"} / {s.ClienteIp ?? "-"}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ERRO: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("2) Sessões que o agente envia ao portal");
        var enviadas = new SessionCollector(NullLogger<SessionCollector>.Instance).Collect();
        if (enviadas.Count == 0) Console.WriteLine("   nenhuma");
        foreach (var s in enviadas)
            Console.WriteLine($"   ID {s.SessionId,-4} {s.Username,-30} estado={s.State,-13} ocioso={s.IdleTimeMinutes} min");

        var desc = enviadas.Count(s => s.State == "Disconnected");
        Console.WriteLine($"   total: {enviadas.Count} sessão(ões), {desc} desconectada(s)");

        Console.WriteLine();
        Console.WriteLine("3) Eventos de sessão das últimas 24 h (1149, 21, 22, 23, 24, 25 + 39/40)");
        var eventos = new SessionEventCollector(NullLogger<SessionEventCollector>.Instance).Coletar();
        if (eventos.Count == 0)
            Console.WriteLine($"   nenhum evento (ou sem permissão para ler os logs TerminalServices)");
        foreach (var e in eventos.TakeLast(40))
            Console.WriteLine($"   {e.TimeUtc.ToLocalTime():dd/MM HH:mm:ss} {e.EventId,5} {Rotulo(e.EventId),-12} " +
                              $"sessão {(e.SessionId == 0 ? "-" : e.SessionId.ToString()),-4} {e.Username,-25} origem={e.ClientIp ?? "-"}" +
                              (e.ReasonCode is { } m ? $" motivo={m}" : "") +
                              (e.DisconnectedBySessionId is { } por ? $" pedida-pela-sessão={por}" : ""));
        if (eventos.Count > 40) Console.WriteLine($"   ... {eventos.Count - 40} evento(s) anteriores omitidos");

        Console.WriteLine();
        Console.WriteLine("4) quser (comparação)");
        try
        {
            var psi = new ProcessStartInfo("quser")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var saida = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            foreach (var linha in saida.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Console.WriteLine("   " + linha.TrimEnd());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ERRO: {ex.Message}");
        }

        return 0;
    }

    private static string Rotulo(int id) => id switch
    {
        1149 => "AUTENTICOU",
        21   => "LOGON",
        22   => "SHELL",
        23   => "LOGOFF",
        24   => "DESCONECTOU",
        25   => "RECONECTOU",
        _    => id.ToString(),
    };

    private static string Usuario(string? dominio, string usuario) =>
        string.IsNullOrWhiteSpace(dominio) ? usuario : $"{dominio}\\{usuario}";
}
