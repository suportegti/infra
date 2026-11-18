using System.Diagnostics;
using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models.Dtos;
using RDSMonitor.Web.Security;

namespace RDSMonitor.Web.Services;

/// <summary>Pasta onde ficam os pacotes do agente (ao lado do banco: data\pacotes-agente).</summary>
public sealed record PastaPacotesAgente(string Caminho);

/// <summary>Instrução devolvida ao agente na resposta do heartbeat.</summary>
public sealed record InstrucaoAtualizacao(string Version, string Sha256, long Size, string Url);

/// <summary>Situação da atualização exibida na tela.</summary>
public static class SituacaoAtualizacao
{
    public const string Aguardando = "Aguardando o agente";
    public const string Enviada    = "Baixando e instalando";
    public const string Concluida  = "Concluída";
    public const string Falhou     = "Falhou";
    public const string Cancelada  = "Cancelada";
}

public interface IAtualizacaoAgenteService
{
    /// <summary>Recebe um RDSMonitor.Agent-x.y.z.zip, confere o conteúdo e guarda. Retorna a versão ou o erro.</summary>
    Task<(bool Ok, string Mensagem)> EnviarPacoteAsync(Stream conteudo, string nomeArquivo, ClaimsPrincipal usuario);
    Task<List<AgentePacote>> ListarPacotesAsync();
    Task<string?> ExcluirPacoteAsync(int id, ClaimsPrincipal usuario);

    /// <summary>Marca os servidores para atualizar para <paramref name="versao"/>. Retorna quantos foram marcados.</summary>
    Task<int> SolicitarAsync(IEnumerable<int> serverIds, string versao, ClaimsPrincipal usuario);
    Task CancelarAsync(int serverId, ClaimsPrincipal usuario);

    /// <summary>Chamado no heartbeat: a instrução de atualização para este servidor, se houver.</summary>
    Task<InstrucaoAtualizacao?> InstrucaoParaAsync(string serverName);

    /// <summary>Arquivo do pacote para o download do agente.</summary>
    Task<(string Caminho, string Nome)?> ArquivoDoPacoteAsync(string versao);

    /// <summary>Chamado no heartbeat: versão informada pelo agente e resultado da última atualização.</summary>
    Task RegistrarHeartbeatAsync(Server server, string? versaoAgente, AtualizacaoResultadoDto? resultado);
}

public class AtualizacaoAgenteService : IAtualizacaoAgenteService
{
    /// <summary>Sem confirmação nesse tempo depois de entregue ao agente, a atualização é dada como falha.</summary>
    public static readonly TimeSpan TempoLimite = TimeSpan.FromMinutes(15);
    private const long TamanhoMaximo = 300L * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly IPermissaoService _permissoes;
    private readonly ILogger<AtualizacaoAgenteService> _log;
    private readonly string _pasta;

    public AtualizacaoAgenteService(AppDbContext db, IPermissaoService permissoes, PastaPacotesAgente pasta,
        ILogger<AtualizacaoAgenteService> log)
    {
        _db = db;
        _permissoes = permissoes;
        _log = log;
        _pasta = pasta.Caminho;
        Directory.CreateDirectory(_pasta);
    }

    // ── Pacotes ───────────────────────────────────────────────

    public async Task<(bool, string)> EnviarPacoteAsync(Stream conteudo, string nomeArquivo, ClaimsPrincipal usuario)
    {
        await ExigirPermissaoAsync(usuario);

        var temp = Path.Combine(_pasta, $".envio-{Guid.NewGuid():N}.tmp");
        try
        {
            // Grava e calcula o hash ao mesmo tempo
            string sha;
            long tamanho;
            await using (var arquivo = File.Create(temp))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int lidos;
                tamanho = 0;
                while ((lidos = await conteudo.ReadAsync(buffer)) > 0)
                {
                    tamanho += lidos;
                    if (tamanho > TamanhoMaximo) return (false, "O arquivo passa de 300 MB — não parece um pacote do agente.");
                    hash.AppendData(buffer, 0, lidos);
                    await arquivo.WriteAsync(buffer.AsMemory(0, lidos));
                }
                sha = Convert.ToHexString(hash.GetHashAndReset());
            }

            var versao = LerVersaoDoPacote(temp, out var erro);
            if (versao is null) return (false, erro!);

            var destino = Path.Combine(_pasta, $"RDSMonitor.Agent-{versao}.zip");
            File.Move(temp, destino, overwrite: true);

            var pacote = await _db.AgentePacotes.FirstOrDefaultAsync(p => p.Versao == versao)
                         ?? _db.AgentePacotes.Add(new AgentePacote { Versao = versao }).Entity;
            pacote.NomeArquivo  = Path.GetFileName(destino);
            pacote.NomeOriginal = Path.GetFileName(nomeArquivo);
            pacote.TamanhoBytes = tamanho;
            pacote.Sha256       = sha;
            pacote.EnviadoEm    = DateTime.UtcNow;
            pacote.EnviadoPor   = Login(usuario);
            await _db.SaveChangesAsync();
            _db.ChangeTracker.Clear();

            _log.LogInformation("Pacote do agente {Versao} recebido de {Usuario} (SHA-256 {Sha}).", versao, pacote.EnviadoPor, sha);
            return (true, versao);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Confere que é um pacote do agente (gerado pelo build-pacotes.ps1) e lê a versão do executável.</summary>
    private static string? LerVersaoDoPacote(string zip, out string? erro)
    {
        erro = null;
        try
        {
            using var arquivo = ZipFile.OpenRead(zip);
            ZipArchiveEntry? Entrada(string nome) => arquivo.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals(nome, StringComparison.OrdinalIgnoreCase));

            var dll = Entrada("app/RDSMonitor.Agent.dll");
            if (Entrada("app/RDSMonitor.Agent.exe") is null || dll is null)
            {
                erro = "Este .zip não é um pacote do agente: falta a pasta app\\ com o RDSMonitor.Agent.exe. Use o RDSMonitor.Agent-x.y.z.zip gerado pelo build-pacotes.ps1.";
                return null;
            }
            if (Entrada("app/Scripts/atualizar-agente.ps1") is null)
            {
                erro = "Este pacote é de uma versão sem atualização automática (falta app\\Scripts\\atualizar-agente.ps1).";
                return null;
            }

            var temp = Path.Combine(Path.GetTempPath(), $"rdsagent-{Guid.NewGuid():N}.dll");
            try
            {
                dll.ExtractToFile(temp);
                var versao = Normalizar(FileVersionInfo.GetVersionInfo(temp).FileVersion);
                if (versao is null) erro = "Não foi possível ler a versão do RDSMonitor.Agent.dll do pacote.";
                return versao;
            }
            finally
            {
                File.Delete(temp);
            }
        }
        catch (InvalidDataException)
        {
            erro = "O arquivo enviado não é um .zip válido.";
            return null;
        }
    }

    public Task<List<AgentePacote>> ListarPacotesAsync() =>
        _db.AgentePacotes.AsNoTracking().ToListAsync()
           .ContinueWith(t => t.Result.OrderByDescending(p => Version.Parse(p.Versao)).ToList());

    public async Task<string?> ExcluirPacoteAsync(int id, ClaimsPrincipal usuario)
    {
        await ExigirPermissaoAsync(usuario);
        var pacote = await _db.AgentePacotes.FindAsync(id);
        if (pacote is null) return "Pacote não encontrado.";
        if (await _db.Servers.AnyAsync(s => s.VersaoAlvo == pacote.Versao))
            return $"Há servidores sendo atualizados para a {pacote.Versao}. Cancele essas atualizações antes de excluir o pacote.";

        var caminho = Path.Combine(_pasta, pacote.NomeArquivo);
        if (File.Exists(caminho)) File.Delete(caminho);
        _db.AgentePacotes.Remove(pacote);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return null;
    }

    // ── Pedidos de atualização ───────────────────────────────

    public async Task<int> SolicitarAsync(IEnumerable<int> serverIds, string versao, ClaimsPrincipal usuario)
    {
        await ExigirPermissaoAsync(usuario);
        versao = Normalizar(versao) ?? throw new ArgumentException("Versão inválida.");
        if (!await _db.AgentePacotes.AnyAsync(p => p.Versao == versao))
            throw new InvalidOperationException($"Não há pacote da versão {versao} no portal.");

        var ids = serverIds.ToList();
        var servidores = await _db.Servers.Where(s => ids.Contains(s.Id) && s.IsEnabled).ToListAsync();
        var marcados = 0;
        foreach (var s in servidores)
        {
            if (Normalizar(s.AgentVersion) == versao) continue;   // já está nessa versão

            var auditoria = NovaAuditoria(usuario, s, $"Agente {s.AgentVersion ?? "desconhecido"} → {versao}");
            _db.Auditoria.Add(auditoria);
            await _db.SaveChangesAsync();

            s.VersaoAlvo             = versao;
            s.AtualizacaoStatus      = SituacaoAtualizacao.Aguardando;
            s.AtualizacaoMensagem    = null;
            s.AtualizacaoEm          = DateTime.UtcNow;
            s.AtualizacaoEnviadaEm   = null;
            s.AtualizacaoAuditoriaId = auditoria.Id;
            marcados++;
        }
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return marcados;
    }

    public async Task CancelarAsync(int serverId, ClaimsPrincipal usuario)
    {
        await ExigirPermissaoAsync(usuario);
        var s = await _db.Servers.FindAsync(serverId);
        if (s?.VersaoAlvo is null) return;
        await Encerrar(s, SituacaoAtualizacao.Cancelada, $"Cancelada por {Login(usuario)}.", CommandStatus.Failed);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    // ── Agente ────────────────────────────────────────────────

    public async Task<InstrucaoAtualizacao?> InstrucaoParaAsync(string serverName)
    {
        var nome = serverName.ToLower();
        var s = await _db.Servers.FirstOrDefaultAsync(x => x.Name.ToLower() == nome);
        if (s?.VersaoAlvo is null) return null;

        if (s.AtualizacaoEnviadaEm is { } enviada && DateTime.UtcNow - enviada > TempoLimite)
        {
            await Encerrar(s, SituacaoAtualizacao.Falhou,
                $"O agente não confirmou a versão {s.VersaoAlvo} em {(int)TempoLimite.TotalMinutes} minutos.", CommandStatus.Failed);
            await _db.SaveChangesAsync();
            return null;
        }

        var pacote = await _db.AgentePacotes.AsNoTracking().FirstOrDefaultAsync(p => p.Versao == s.VersaoAlvo);
        if (pacote is null || !File.Exists(Path.Combine(_pasta, pacote.NomeArquivo)))
        {
            await Encerrar(s, SituacaoAtualizacao.Falhou, $"O pacote da versão {s.VersaoAlvo} não está mais no portal.", CommandStatus.Failed);
            await _db.SaveChangesAsync();
            return null;
        }

        if (s.AtualizacaoEnviadaEm is null)
        {
            s.AtualizacaoEnviadaEm = DateTime.UtcNow;
            s.AtualizacaoStatus    = SituacaoAtualizacao.Enviada;
            await AtualizarAuditoria(s.AtualizacaoAuditoriaId, CommandStatus.Executing, null);
            await _db.SaveChangesAsync();
        }

        return new InstrucaoAtualizacao(pacote.Versao, pacote.Sha256, pacote.TamanhoBytes, $"api/agent/pacote/{pacote.Versao}");
    }

    public async Task<(string Caminho, string Nome)?> ArquivoDoPacoteAsync(string versao)
    {
        versao = Normalizar(versao) ?? string.Empty;
        var pacote = await _db.AgentePacotes.AsNoTracking().FirstOrDefaultAsync(p => p.Versao == versao);
        if (pacote is null) return null;
        var caminho = Path.Combine(_pasta, pacote.NomeArquivo);
        return File.Exists(caminho) ? (caminho, pacote.NomeArquivo) : null;
    }

    /// <summary>Atualiza a versão do agente e conclui (ou dá como falha) a atualização em andamento.</summary>
    public async Task RegistrarHeartbeatAsync(Server server, string? versaoAgente, AtualizacaoResultadoDto? resultado)
    {
        if (Normalizar(versaoAgente) is { } versao) server.AgentVersion = versao;
        if (server.VersaoAlvo is null) return;

        if (resultado is not null && Normalizar(resultado.Versao) == server.VersaoAlvo && !resultado.Sucesso)
        {
            await Encerrar(server, SituacaoAtualizacao.Falhou, Limitar(resultado.Mensagem, 500) ?? "Falha sem detalhe.", CommandStatus.Failed);
            return;
        }

        if (server.AgentVersion == server.VersaoAlvo)
            await Encerrar(server, SituacaoAtualizacao.Concluida, $"Agente atualizado para {server.VersaoAlvo}.", CommandStatus.Completed);
    }

    // ── Auxiliares ────────────────────────────────────────────

    private async Task Encerrar(Server s, string situacao, string mensagem, CommandStatus statusAuditoria)
    {
        await AtualizarAuditoria(s.AtualizacaoAuditoriaId, statusAuditoria, mensagem);
        s.AtualizacaoStatus      = situacao;
        s.AtualizacaoMensagem    = mensagem;
        s.AtualizacaoEm          = DateTime.UtcNow;
        s.VersaoAlvo             = null;
        s.AtualizacaoEnviadaEm   = null;
        s.AtualizacaoAuditoriaId = null;
        _log.LogInformation("Atualização do agente de {Server}: {Situacao} — {Mensagem}", s.Name, situacao, mensagem);
    }

    private async Task AtualizarAuditoria(long? id, CommandStatus status, string? resultado)
    {
        if (id is null) return;
        var a = await _db.Auditoria.FindAsync(id.Value);
        if (a is null) return;
        a.Status = status;
        if (resultado is not null)
        {
            a.Resultado   = Limitar(resultado, 2000);
            a.ConcluidoEm = DateTime.UtcNow;
        }
    }

    private static AuditoriaAcao NovaAuditoria(ClaimsPrincipal u, Server s, string detalhes) => new()
    {
        DataHora       = DateTime.UtcNow,
        OperadorLogin  = Login(u),
        OperadorNome   = u.Identity?.Name,
        OperadorPerfil = u.FindFirst(ClaimTypes.Role)?.Value,
        Acao           = CommandType.AtualizarAgente,
        ServidorId     = s.Id,
        ServidorNome   = s.Name,
        Detalhes       = detalhes,
        Status         = CommandStatus.Pending,
    };

    private async Task ExigirPermissaoAsync(ClaimsPrincipal usuario)
    {
        if (!await _permissoes.PossuiAsync(usuario, Permissoes.AgentesAtualizar))
            throw new UnauthorizedAccessException("Seu perfil não tem a permissão \"Atualizar agentes\".");
    }

    private static string Login(ClaimsPrincipal u) => u.FindFirst("username")?.Value ?? u.Identity?.Name ?? "?";

    private static string? Limitar(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    /// <summary>"1.7" / "1.7.0" / "1.7.0.0" → "1.7.0.0".</summary>
    public static string? Normalizar(string? texto)
    {
        if (!Version.TryParse(texto?.Split('+', '-', ' ')[0], out var v)) return null;
        return new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0)).ToString();
    }
}
