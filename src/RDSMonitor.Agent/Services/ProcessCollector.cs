using System.Collections.Concurrent;
using System.Management;
using System.Security.Principal;
using RDSMonitor.Agent.Models;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// Coleta os processos das sessões de usuário (SessionId &gt; 0): nome, PID,
/// dono, sessão RDS, memória e CPU — via <see cref="WtsProcessApi"/>
/// (WTSEnumerateProcessesEx), que traz tudo numa única chamada rápida.
///
/// A CPU de cada processo é a diferença do tempo de CPU (User + Kernel) entre
/// duas coletas, dividida pelo tempo decorrido e pelo número de núcleos — o
/// mesmo número do Gerenciador de Tarefas. Na primeira coleta sai 0.
/// </summary>
public class ProcessCollector
{
    private readonly ILogger<ProcessCollector> _log;

    // Chave = PID + nome (reduz o risco de comparar um PID reutilizado pelo Windows)
    private Dictionary<(int Pid, string Nome), (ulong CpuTicks, DateTime Em)> _amostraAnterior = new();

    // SID → nome da conta. A tradução consulta o AD/LSA, por isso fica em cache.
    private static readonly ConcurrentDictionary<string, string?> _contaPorSid = new();

    public ProcessCollector(ILogger<ProcessCollector> log) => _log = log;

    public record OwnerInfo(string? Username, string? Sid);

    public List<ProcessInfo> Collect()
    {
        var result      = new List<ProcessInfo>();
        var novaAmostra = new Dictionary<(int, string), (ulong, DateTime)>();
        var agora       = DateTime.UtcNow;
        var nucleos     = Environment.ProcessorCount;

        try
        {
            foreach (var p in WtsProcessApi.Enumerate())
            {
                if (p.SessionId <= 0) continue;   // sessão 0 = serviços

                var nome  = NomeSemExe(p.Name);
                var chave = (p.Pid, nome);
                novaAmostra[chave] = (p.CpuTicks, agora);

                double cpu = 0;
                if (_amostraAnterior.TryGetValue(chave, out var anterior))
                {
                    var decorrido = (agora - anterior.Em).Ticks;   // 1 tick = 100 ns, mesma unidade da API
                    if (decorrido > 0 && p.CpuTicks >= anterior.CpuTicks)
                        cpu = (double)(p.CpuTicks - anterior.CpuTicks) / (decorrido * nucleos) * 100;
                }

                result.Add(new ProcessInfo
                {
                    Pid          = p.Pid,
                    Name         = nome,
                    Username     = NomeDaConta(p.OwnerSid),
                    RdsSessionId = p.SessionId,
                    MemoryMb     = Math.Round(p.WorkingSetBytes / (1024.0 * 1024), 1),
                    CpuPercent   = Math.Round(Math.Min(cpu, 100), 1),
                    IsProtected  = ProcessProtection.IsProtected(nome, p.OwnerSid, p.SessionId),
                });
            }

            _amostraAnterior = novaAmostra;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Erro ao coletar processos (WTSEnumerateProcessesEx).");
        }

        return result;
    }

    /// <summary>Dono atual de um PID (usado na validação antes de derrubar).</summary>
    public static OwnerInfo? GetOwnerByPid(int pid)
    {
        try
        {
            foreach (var p in WtsProcessApi.Enumerate())
                if (p.Pid == pid) return new OwnerInfo(NomeDaConta(p.OwnerSid), p.OwnerSid);
        }
        catch { /* falha na API — tratado como sem dono (protegido) */ }
        return null;
    }

    /// <summary>"DOMINIO\usuario" → "usuario"; null se o SID não puder ser traduzido.</summary>
    private static string? NomeDaConta(string? sid)
    {
        if (sid is null) return null;
        return _contaPorSid.GetOrAdd(sid, s =>
        {
            try
            {
                var conta = new SecurityIdentifier(s).Translate(typeof(NTAccount)).Value;
                var barra = conta.IndexOf('\\');
                return barra >= 0 ? conta[(barra + 1)..] : conta;
            }
            catch { return null; }   // conta excluída/órfã
        });
    }

    private static string NomeSemExe(string nome)
        => nome.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? nome[..^4] : nome;

    /// <summary>
    /// Retorna o percentual de CPU total do servidor usando WMI.
    /// É feita uma única leitura — não é média, mas é suficiente para o dashboard.
    /// </summary>
    public static double GetTotalCpuPercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT LoadPercentage FROM Win32_Processor");

            double total = 0;
            int    count = 0;

            foreach (ManagementObject obj in searcher.Get())
            {
                total += Convert.ToDouble(obj["LoadPercentage"]);
                count++;
            }

            return count > 0 ? Math.Round(total / count, 1) : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Retorna (memóriaUsadaGb, memóriaTotalGb) via WMI Win32_OperatingSystem.
    /// </summary>
    public static (double UsedGb, double TotalGb) GetMemoryInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");

            foreach (ManagementObject obj in searcher.Get())
            {
                // Valores em KB
                var totalKb = Convert.ToDouble(obj["TotalVisibleMemorySize"]);
                var freeKb  = Convert.ToDouble(obj["FreePhysicalMemory"]);

                var totalGb = Math.Round(totalKb / (1024 * 1024), 2);
                var usedGb  = Math.Round((totalKb - freeKb) / (1024 * 1024), 2);

                return (usedGb, totalGb);
            }
        }
        catch { /* ignora */ }

        return (0, 0);
    }
}
