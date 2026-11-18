using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting.WindowsServices;
using RDSMonitor.Agent.Models;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// Atualização automática do agente, pedida pelo portal (tela Atualização de agentes).
///
/// 1. O portal responde ao heartbeat com a versão nova, o hash SHA-256 e o endereço do pacote.
/// 2. O agente baixa o .zip do próprio portal (com a chave X-Agent-Key), confere o hash e
///    a versão do executável, e extrai numa pasta de trabalho (_atualizacao).
/// 3. Como um serviço não consegue trocar os próprios arquivos, o agente inicia o script
///    Scripts\atualizar-agente.ps1, que para o serviço, faz backup, copia a versão nova
///    (preservando o appsettings.json) e religa o serviço.
/// 4. O agente novo grava _atualizacao\ok.txt no primeiro heartbeat aceito. Sem essa
///    confirmação em 3 min, o script restaura o backup e religa a versão anterior.
/// 5. O resultado (sucesso ou falha) fica em _atualizacao\resultado.json e vai ao portal
///    no heartbeat seguinte.
/// </summary>
public sealed class AgentUpdater
{
    public const string NomeServico = "RDSMonitor.Agent";
    public const string PastaTrabalhoNome = "_atualizacao";

    public static readonly Version VersaoAtual =
        Normalizar(typeof(AgentUpdater).Assembly.GetName().Version) ?? new Version(0, 0, 0, 0);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly ApiClient _api;
    private readonly IHostEnvironment _env;
    private readonly ILogger<AgentUpdater> _log;
    private readonly string _base = AppContext.BaseDirectory.TrimEnd('\\', '/');
    private int _emAndamento;

    public AgentUpdater(ApiClient api, IHostEnvironment env, ILogger<AgentUpdater> log)
    {
        _api = api;
        _env = env;
        _log = log;
    }

    private string Trabalho      => Path.Combine(_base, PastaTrabalhoNome);
    private string ArqResultado  => Path.Combine(Trabalho, "resultado.json");
    private string ArqPendente   => Path.Combine(Trabalho, "pendente.json");
    private string ArqConfirmado => Path.Combine(Trabalho, "ok.txt");

    // ── Comunicação com o portal ──────────────────────────────────────────────

    /// <summary>Resultado da última atualização ainda não entregue ao portal.</summary>
    public AtualizacaoResultado? ResultadoPendente()
    {
        try
        {
            return File.Exists(ArqResultado)
                ? JsonSerializer.Deserialize<AtualizacaoResultado>(File.ReadAllText(ArqResultado), Json)
                : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Resultado de atualização ilegível ({Msg}); descartado.", ex.Message);
            TentarApagar(ArqResultado);
            return null;
        }
    }

    public void ResultadoEntregue() => TentarApagar(ArqResultado);

    /// <summary>
    /// Chamado a cada heartbeat aceito: se esta é a versão que acabou de ser
    /// instalada, avisa o script de atualização que o agente novo está funcionando.
    /// </summary>
    public void ConfirmarFuncionamento()
    {
        try
        {
            if (!File.Exists(ArqPendente) || File.Exists(ArqConfirmado)) return;
            var alvo = JsonSerializer.Deserialize<Pendente>(File.ReadAllText(ArqPendente), Json);
            if (alvo is not null && Normalizar(alvo.Versao) == VersaoAtual)
            {
                File.WriteAllText(ArqConfirmado, DateTime.UtcNow.ToString("o"));
                _log.LogInformation("Versão {Versao} em funcionamento — atualização confirmada.", VersaoAtual);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Não foi possível confirmar a atualização: {Msg}", ex.Message);
        }
    }

    /// <summary>Inicia a atualização em segundo plano (uma de cada vez).</summary>
    public void Iniciar(AtualizacaoInstrucao instrucao)
    {
        var alvo = Normalizar(instrucao.Version);
        if (alvo is null || alvo <= VersaoAtual) return;
        if (Interlocked.CompareExchange(ref _emAndamento, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            var iniciou = false;
            try
            {
                iniciou = await AtualizarAsync(instrucao, alvo);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha na atualização para {Versao}.", alvo);
                GravarResultado(alvo, false, ex.Message);
            }
            finally
            {
                // Se o script assumiu, este processo vai ser encerrado por ele; senão, libera nova tentativa
                if (!iniciou) Interlocked.Exchange(ref _emAndamento, 0);
            }
        });
    }

    // ── Atualização ───────────────────────────────────────────────────────────

    private async Task<bool> AtualizarAsync(AtualizacaoInstrucao instrucao, Version alvo)
    {
        var comoServico = WindowsServiceHelpers.IsWindowsService();
        if (!comoServico && !_env.IsDevelopment())
        {
            GravarResultado(alvo, false,
                "O agente não está rodando como serviço do Windows; atualize-o pelo instalador.");
            return false;
        }

        _log.LogInformation("Portal pediu atualização {Atual} → {Alvo}. Baixando o pacote…", VersaoAtual, alvo);
        Directory.CreateDirectory(Trabalho);
        TentarApagar(ArqConfirmado);
        TentarApagar(ArqResultado);

        // 1. Download e conferência do hash
        var zip = Path.Combine(Trabalho, $"RDSMonitor.Agent-{alvo}.zip");
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10)))
            await _api.BaixarAsync(instrucao.Url, zip, cts.Token);

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        if (!hash.Equals(instrucao.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"O pacote baixado está corrompido (SHA-256 {hash[..12]}… diferente do esperado).");

        // 2. Extração e conferência da versão do executável
        var pasta = Path.Combine(Trabalho, alvo.ToString());
        if (Directory.Exists(pasta)) Directory.Delete(pasta, recursive: true);
        ZipFile.ExtractToDirectory(zip, pasta);

        var app = Path.Combine(pasta, "app");
        var dll = Path.Combine(app, "RDSMonitor.Agent.dll");
        if (!File.Exists(Path.Combine(app, "RDSMonitor.Agent.exe")) || !File.Exists(dll))
            throw new InvalidOperationException("O pacote não tem a pasta app\\ com o RDSMonitor.Agent.exe.");

        var versaoPacote = Normalizar(FileVersionInfo.GetVersionInfo(dll).FileVersion);
        if (versaoPacote != alvo)
            throw new InvalidOperationException($"O pacote contém a versão {versaoPacote}, não a {alvo}.");

        // 3. Script de troca (copiado para fora da pasta que vai ser substituída)
        var scriptOrigem = Path.Combine(_base, "Scripts", "atualizar-agente.ps1");
        if (!File.Exists(scriptOrigem))
            throw new InvalidOperationException("Script de atualização não encontrado na instalação atual (Scripts\\atualizar-agente.ps1).");
        var script = Path.Combine(Trabalho, "atualizar-agente.ps1");
        File.Copy(scriptOrigem, script, overwrite: true);

        File.WriteAllText(ArqPendente, JsonSerializer.Serialize(new Pendente { Versao = alvo.ToString() }, Json));

        var argumentos = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" " +
                         $"-Origem \"{app}\" -Destino \"{_base}\" -Versao \"{alvo}\" -Trabalho \"{Trabalho}\" " +
                         (comoServico ? $"-Servico \"{NomeServico}\"" : $"-ProcessoId {Environment.ProcessId}");

        Process.Start(new ProcessStartInfo("powershell.exe", argumentos)
        {
            UseShellExecute  = false,
            CreateNoWindow   = true,
            WorkingDirectory = Trabalho,
        });

        _log.LogWarning("Pacote {Alvo} conferido. O agente será reiniciado pelo script de atualização " +
                        "(log em {Log}).", alvo, Path.Combine(Trabalho, "atualizacao.log"));
        return true;
    }

    // ── Auxiliares ────────────────────────────────────────────────────────────

    private void GravarResultado(Version alvo, bool sucesso, string mensagem)
    {
        try
        {
            Directory.CreateDirectory(Trabalho);
            File.WriteAllText(ArqResultado, JsonSerializer.Serialize(new AtualizacaoResultado
            {
                Versao = alvo.ToString(), Sucesso = sucesso, Mensagem = mensagem, Em = DateTime.UtcNow,
            }, Json));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Não foi possível gravar o resultado da atualização: {Msg}", ex.Message);
        }
    }

    private static void TentarApagar(string arquivo)
    {
        try { if (File.Exists(arquivo)) File.Delete(arquivo); } catch { /* tenta de novo no próximo ciclo */ }
    }

    /// <summary>"1.7" / "1.7.0" / "1.7.0.0" → 1.7.0.0 (comparável).</summary>
    public static Version? Normalizar(string? texto)
        => Version.TryParse(texto?.Split('+', '-', ' ')[0], out var v) ? Normalizar(v) : null;

    private static Version? Normalizar(Version? v)
        => v is null ? null : new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private sealed class Pendente
    {
        public string Versao { get; set; } = string.Empty;
    }
}
