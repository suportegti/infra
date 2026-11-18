using System.Net.Http.Json;
using System.Text.Json;
using RDSMonitor.Agent.Models;
using RDSMonitor.Agent.Settings;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// Responsável por toda comunicação HTTP entre o agente e a API central.
/// Recebe um <see cref="HttpClient"/> já configurado (BaseAddress + X-Agent-Key)
/// registrado como singleton em Program.cs — sem dependência de IHttpClientFactory.
/// </summary>
public class ApiClient
{
    private readonly HttpClient          _http;
    private readonly AgentSettings       _settings;
    private readonly ILogger<ApiClient>  _log;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // HttpClient injetado diretamente — registrado como singleton em Program.cs
    public ApiClient(HttpClient http, AgentSettings settings, ILogger<ApiClient> log)
    {
        _http     = http;
        _settings = settings;
        _log      = log;
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Envia o estado completo do servidor (sessões + processos) para a API.
    /// POST /api/agent/heartbeat
    /// </summary>
    /// <returns>A resposta do portal (null se o heartbeat falhou ou foi recusado).</returns>
    public async Task<HeartbeatResposta?> SendHeartbeatAsync(AgentHeartbeat heartbeat, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(
                "/api/agent/heartbeat", heartbeat, _jsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Heartbeat rejeitado pela API: {Status}", response.StatusCode);
                return null;
            }

            _log.LogDebug("Heartbeat enviado: {Sessions} sessão(ões).", heartbeat.Sessions.Count);
            try
            {
                return await response.Content.ReadFromJsonAsync<HeartbeatResposta>(_jsonOptions, ct) ?? new HeartbeatResposta();
            }
            catch (JsonException)
            {
                return new HeartbeatResposta();   // portal antigo: resposta sem os campos novos
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Falha ao enviar heartbeat: {Msg}", ex.Message);
            return null;
        }
    }

    /// <summary>Baixa um arquivo do portal (com a chave do agente) para <paramref name="destino"/>.</summary>
    public async Task BaixarAsync(string url, string destino, CancellationToken ct)
    {
        using var resposta = await _http.GetAsync(url.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead, ct);
        resposta.EnsureSuccessStatusCode();
        await using var origem  = await resposta.Content.ReadAsStreamAsync(ct);
        await using var arquivo = File.Create(destino);
        await origem.CopyToAsync(arquivo, ct);
    }

    // ── Comandos ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Busca comandos pendentes para este servidor.
    /// GET /api/agent/commands/{serverName}
    /// </summary>
    public async Task<List<CommandDto>> GetPendingCommandsAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetFromJsonAsync<List<CommandDto>>(
                $"/api/agent/commands/{_settings.ServerName}", _jsonOptions, ct);

            return response ?? new();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Falha ao buscar comandos: {Msg}", ex.Message);
            return new();
        }
    }

    /// <summary>
    /// Reporta o resultado de um comando executado.
    /// POST /api/agent/commands/{commandId}/result
    /// </summary>
    public async Task ReportCommandResultAsync(int commandId, CommandResult result, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(
                $"/api/agent/commands/{commandId}/result", result, _jsonOptions, ct);

            if (!response.IsSuccessStatusCode)
                _log.LogWarning("Falha ao reportar resultado do comando {Id}: {Status}",
                    commandId, response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Falha ao reportar resultado do comando {Id}: {Msg}", commandId, ex.Message);
        }
    }
}
