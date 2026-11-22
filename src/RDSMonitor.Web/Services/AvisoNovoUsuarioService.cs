using System.Net;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Services;

/// <summary>Resultado do e-mail de boas-vindas (vai na URL de retorno da tela Usuários).</summary>
public enum ResultadoAviso { Enviado, Falhou, Desligado, SemEmail }

public interface IAvisoNovoUsuarioService
{
    /// <summary>Se o portal vai mandar o e-mail ao criar usuários (envio configurado e opção ligada).</summary>
    Task<bool> AtivoAsync();

    /// <summary>
    /// Envia o e-mail de boas-vindas. Conta local: link do portal, login e a senha temporária
    /// (quando gerada pelo portal). Conta Microsoft 365: só o link e o aviso para entrar pelo botão da Microsoft.
    /// </summary>
    Task<ResultadoAviso> EnviarAsync(string username, string displayName, string? email, TipoAutenticacao tipo,
        string? senhaTemporaria, string? enderecoRequisicao, string operador, string? ip);
}

public class AvisoNovoUsuarioService : IAvisoNovoUsuarioService
{
    private readonly IEmailService _email;
    private readonly IConfiguracaoService _config;
    private readonly ILogger<AvisoNovoUsuarioService> _log;

    public AvisoNovoUsuarioService(IEmailService email, IConfiguracaoService config, ILogger<AvisoNovoUsuarioService> log)
    {
        _email = email;
        _config = config;
        _log = log;
    }

    public async Task<bool> AtivoAsync() =>
        (await _config.EmailAsync()).AvisarNovoUsuario && await _email.HabilitadoAsync();

    public async Task<ResultadoAviso> EnviarAsync(string username, string displayName, string? email, TipoAutenticacao tipo,
        string? senhaTemporaria, string? enderecoRequisicao, string operador, string? ip)
    {
        if (string.IsNullOrWhiteSpace(email)) return ResultadoAviso.SemEmail;
        if (!await AtivoAsync()) return ResultadoAviso.Desligado;

        var portal = await _email.EnderecoPortalAsync(enderecoRequisicao);
        string H(string? v) => WebUtility.HtmlEncode(v ?? "");
        var link = $"{portal}/login";

        string corpo;
        if (tipo == TipoAutenticacao.Microsoft365)
        {
            corpo = $"""
                <p>Seu acesso ao <strong>RDSManager</strong> foi criado.</p>
                <p>Entre pelo botão <strong>Entrar com Microsoft 365</strong>, usando a sua conta da empresa
                   (<strong>{H(email)}</strong>). Não há senha separada para o portal.</p>
                """;
        }
        else if (senhaTemporaria is not null)
        {
            var politica = await _config.PoliticaSenhaAsync();
            corpo = $"""
                <p>Seu acesso ao <strong>RDSManager</strong> foi criado.</p>
                <table style="border-collapse:collapse;margin:8px 0">
                  <tr><td style="padding:4px 12px 4px 0;color:#6b7280">Usuário</td>
                      <td style="padding:4px 0;font-family:Consolas,monospace;font-size:16px"><strong>{H(username)}</strong></td></tr>
                  <tr><td style="padding:4px 12px 4px 0;color:#6b7280">Senha temporária</td>
                      <td style="padding:4px 0;font-family:Consolas,monospace;font-size:16px;letter-spacing:1px"><strong>{H(senhaTemporaria)}</strong></td></tr>
                </table>
                <p>No primeiro acesso você vai criar a sua senha pessoal (mínimo de 8 caracteres).</p>
                """;
        }
        else
        {
            corpo = $"""
                <p>Seu acesso ao <strong>RDSManager</strong> foi criado com o usuário <strong>{H(username)}</strong>.</p>
                <p>A senha inicial será informada pelo administrador.</p>
                """;
        }

        var html = $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2937">
              <p>Olá, {H(displayName)}.</p>
              {corpo}
              <p><a href="{H(link)}" style="display:inline-block;background:#2563eb;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none">Acessar o RDSManager</a></p>
              <p style="color:#6b7280;font-size:12px">Endereço: {H(link)}<br />Este acesso foi criado por {H(operador)}. Se você não esperava este e-mail, avise a equipe de TI.</p>
            </div>
            """;

        try
        {
            await _email.EnviarAsync(email.Trim(), "RDSManager — seu acesso foi criado", html,
                new ContextoEmail(OrigemEmail.NovoUsuario, username, operador, ip));
            return ResultadoAviso.Enviado;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao enviar o e-mail de boas-vindas de {Usuario}.", username);
            return ResultadoAviso.Falhou;
        }
    }
}
