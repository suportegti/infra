using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace RDSMonitor.Web.Services;

/// <summary>Usuário do portal visto recentemente.</summary>
public sealed record UsuarioOnline(
    string   Username,
    string   Nome,
    string?  Ip,
    DateTime EntrouEm,        // UTC
    DateTime UltimaAtividade, // UTC
    int      TelasAbertas);

/// <summary>
/// Quem está usando o portal agora. Mantido em memória (zera quando o serviço
/// reinicia): cada requisição autenticada e cada tela interativa aberta
/// (circuito do Blazor) marcam o usuário como presente.
///
/// Online = tem tela interativa conectada OU fez alguma requisição nos
/// últimos <see cref="JanelaInatividade"/> minutos. Sair do portal remove na hora.
/// </summary>
public sealed class PresencaService
{
    public static readonly TimeSpan JanelaInatividade = TimeSpan.FromMinutes(5);

    private sealed class Registro
    {
        public required string Username;
        public string   Nome = string.Empty;
        public string?  Ip;
        public DateTime EntrouEm;
        public DateTime UltimaAtividade;
        public readonly ConcurrentDictionary<string, byte> Circuitos = new();
    }

    private readonly ConcurrentDictionary<string, Registro> _usuarios = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Login concluído.</summary>
    public void Entrou(string username, string nome, string? ip)
    {
        var agora = DateTime.UtcNow;
        var r = _usuarios.AddOrUpdate(username,
            _ => new Registro { Username = username, EntrouEm = agora },
            (_, existente) => { existente.EntrouEm = agora; return existente; });
        r.Nome = nome;
        r.Ip = ip;
        r.UltimaAtividade = agora;
    }

    /// <summary>Requisição autenticada (página, formulário, exportação).</summary>
    public void Viu(string username, string nome, string? ip)
    {
        var agora = DateTime.UtcNow;
        var r = _usuarios.GetOrAdd(username, _ => new Registro { Username = username, EntrouEm = agora });
        r.Nome = nome;
        if (ip is not null) r.Ip = ip;
        r.UltimaAtividade = agora;
    }

    public void Saiu(string username) => _usuarios.TryRemove(username, out _);

    public void CircuitoConectado(string username, string nome, string circuitId)
    {
        Viu(username, nome, ip: null);
        if (_usuarios.TryGetValue(username, out var r)) r.Circuitos[circuitId] = 0;
    }

    public void CircuitoDesconectado(string username, string circuitId)
    {
        if (!_usuarios.TryGetValue(username, out var r)) return;
        r.Circuitos.TryRemove(circuitId, out _);
        r.UltimaAtividade = DateTime.UtcNow;
    }

    public List<UsuarioOnline> Online()
    {
        var limite = DateTime.UtcNow - JanelaInatividade;
        return _usuarios.Values
            .Where(r => !r.Circuitos.IsEmpty || r.UltimaAtividade >= limite)
            .Select(r => new UsuarioOnline(r.Username, r.Nome, r.Ip, r.EntrouEm, r.UltimaAtividade, r.Circuitos.Count))
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool EstaOnline(string username)
    {
        if (!_usuarios.TryGetValue(username, out var r)) return false;
        return !r.Circuitos.IsEmpty || r.UltimaAtividade >= DateTime.UtcNow - JanelaInatividade;
    }

    public static string? Login(ClaimsPrincipal u) => u.Identity?.IsAuthenticated == true ? u.FindFirst("username")?.Value : null;
}

/// <summary>Marca o usuário como presente enquanto uma tela interativa (Sessões, Dashboard...) está aberta.</summary>
public sealed class PresencaCircuitHandler : CircuitHandler
{
    private readonly PresencaService _presenca;
    private readonly AuthenticationStateProvider _auth;
    private string? _username;

    public PresencaCircuitHandler(PresencaService presenca, AuthenticationStateProvider auth)
    {
        _presenca = presenca;
        _auth     = auth;
    }

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken ct)
    {
        try
        {
            var user = (await _auth.GetAuthenticationStateAsync()).User;
            _username = PresencaService.Login(user);
            if (_username is not null)
                _presenca.CircuitoConectado(_username, user.Identity?.Name ?? _username, circuit.Id);
        }
        catch (InvalidOperationException)
        {
            // Estado de autenticação ainda não disponível neste circuito
        }
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken ct)
    {
        if (_username is not null) _presenca.CircuitoDesconectado(_username, circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        if (_username is not null) _presenca.CircuitoDesconectado(_username, circuit.Id);
        return Task.CompletedTask;
    }
}
