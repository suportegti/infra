using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace RDSMonitor.Web.Services;

public interface IEmailService
{
    /// <summary>Se o envio de e-mail está configurado (tela Configurações).</summary>
    Task<bool> HabilitadoAsync();

    /// <summary>Envia um e-mail em HTML. Lança exceção com o motivo se falhar.</summary>
    Task EnviarAsync(string para, string assunto, string html);

    /// <summary>
    /// Obtém um token do Microsoft Entra ID com a chave do aplicativo (client credentials).
    /// Usado pelo envio via Microsoft 365 e pelo botão "Testar conexão".
    /// </summary>
    Task<string> TokenAplicativoM365Async(string? tenantId = null, string? clientId = null, string? segredo = null);
}

/// <summary>
/// Envio de e-mail por SMTP (System.Net.Mail — porta 587/25 com STARTTLS) ou pelo
/// Microsoft 365 via Microsoft Graph (POST /users/{remetente}/sendMail), usando o
/// mesmo aplicativo registrado para o login (permissão de aplicativo Mail.Send).
/// </summary>
public class EmailService : IEmailService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly IConfiguracaoService _config;
    private readonly ILogger<EmailService> _log;

    public EmailService(IConfiguracaoService config, ILogger<EmailService> log)
    {
        _config = config;
        _log = log;
    }

    public async Task<bool> HabilitadoAsync()
    {
        var c = await _config.EmailAsync();
        return c.Metodo switch
        {
            MetodoEmail.Smtp         => !string.IsNullOrWhiteSpace(c.SmtpServidor) && !string.IsNullOrWhiteSpace(c.Remetente),
            MetodoEmail.Microsoft365 => (await _config.M365Async()).Configurado && !string.IsNullOrWhiteSpace(c.Remetente),
            _                        => false,
        };
    }

    public async Task EnviarAsync(string para, string assunto, string html)
    {
        var c = await _config.EmailAsync();
        if (string.IsNullOrWhiteSpace(c.Remetente))
            throw new InvalidOperationException("Informe o e-mail do remetente em Configurações > E-mail.");

        switch (c.Metodo)
        {
            case MetodoEmail.Smtp:
                await EnviarSmtpAsync(c, para, assunto, html);
                break;
            case MetodoEmail.Microsoft365:
                await EnviarGraphAsync(c, para, assunto, html);
                break;
            default:
                throw new InvalidOperationException("O envio de e-mail está desligado em Configurações > E-mail.");
        }
        _log.LogInformation("E-mail \"{Assunto}\" enviado para {Para} via {Metodo}.", assunto, para, c.Metodo);
    }

    private async Task EnviarSmtpAsync(ConfigEmail c, string para, string assunto, string html)
    {
        if (string.IsNullOrWhiteSpace(c.SmtpServidor))
            throw new InvalidOperationException("Informe o servidor SMTP em Configurações > E-mail.");
        if (c.SmtpPorta == 465)
            throw new InvalidOperationException("A porta 465 (SSL implícito) não é suportada; use a 587 com TLS (STARTTLS).");

        using var msg = new MailMessage
        {
            From = new MailAddress(c.Remetente!, c.NomeRemetente),
            Subject = assunto,
            Body = html,
            IsBodyHtml = true,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };
        msg.To.Add(para);

        using var smtp = new SmtpClient(c.SmtpServidor, c.SmtpPorta)
        {
            EnableSsl = c.SmtpSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30_000,
        };
        if (!string.IsNullOrWhiteSpace(c.SmtpUsuario))
            smtp.Credentials = new NetworkCredential(c.SmtpUsuario, await _config.SenhaSmtpAsync());

        await smtp.SendMailAsync(msg);
    }

    private async Task EnviarGraphAsync(ConfigEmail c, string para, string assunto, string html)
    {
        var token = await TokenAplicativoM365Async();
        var corpo = JsonSerializer.Serialize(new
        {
            message = new
            {
                subject = assunto,
                body = new { contentType = "HTML", content = html },
                toRecipients = new[] { new { emailAddress = new { address = para } } },
            },
            saveToSentItems = false,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(c.Remetente!)}/sendMail")
        {
            Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var erro = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"O Microsoft 365 recusou o envio ({(int)resp.StatusCode}). Verifique a permissão de aplicativo Mail.Send " +
                $"(com consentimento do administrador) e se o remetente existe. Resposta: {Resumo(erro)}");
        }
    }

    public async Task<string> TokenAplicativoM365Async(string? tenantId = null, string? clientId = null, string? segredo = null)
    {
        var m = await _config.M365Async();
        tenantId ??= m.TenantId;
        clientId ??= m.ClientId;
        segredo  ??= await _config.SegredoM365Async();
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(segredo))
            throw new InvalidOperationException("Preencha Tenant, ID do aplicativo e a chave em Configurações > Microsoft 365.");

        using var resp = await Http.PostAsync(
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = segredo,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials",
            }));
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            string? descricao = null;
            try { descricao = JsonDocument.Parse(json).RootElement.GetProperty("error_description").GetString(); } catch { }
            throw new InvalidOperationException("O Microsoft Entra ID recusou as credenciais: " + Resumo(descricao ?? json));
        }
        return JsonDocument.Parse(json).RootElement.GetProperty("access_token").GetString()!;
    }

    private static string Resumo(string texto)
    {
        var linha = texto.Split('\n', '\r').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? texto;
        return linha.Length > 300 ? linha[..300] + "…" : linha;
    }
}
