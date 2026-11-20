using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;

namespace RDSMonitor.Web.Services;

public enum MetodoEmail { Desligado, Smtp, Microsoft365 }

/// <summary>Política de senha das contas locais.</summary>
public sealed record PoliticaSenha(int ValidadeDias);

/// <summary>Envio de e-mail (recuperação de senha e teste).</summary>
public sealed record ConfigEmail(
    MetodoEmail Metodo,
    string? Remetente, string? NomeRemetente,
    string? SmtpServidor, int SmtpPorta, bool SmtpSsl, string? SmtpUsuario, bool SmtpTemSenha);

/// <summary>Integração com o Microsoft 365 (Entra ID): login e, opcionalmente, envio de e-mail.</summary>
public sealed record ConfigM365(
    bool LoginHabilitado,
    string? TenantId, string? ClientId, bool TemSegredo,
    DateTime? SegredoExpiraEm, DateTime? SegredoAlteradoEm, string? SegredoAlteradoPor)
{
    public bool Configurado => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId) && TemSegredo;

    /// <summary>Dias até a chave do aplicativo vencer (negativo = vencida; null = data não informada).</summary>
    public int? DiasParaExpirar => SegredoExpiraEm is { } d ? (int)Math.Floor((d.Date - Horario.Hoje).TotalDays) : null;

    public bool ExpiraEmBreve => DiasParaExpirar is { } dias && dias <= 30;
}

public interface IConfiguracaoService
{
    Task<PoliticaSenha> PoliticaSenhaAsync();
    Task<ConfigEmail> EmailAsync();
    Task<ConfigM365> M365Async();

    /// <summary>Segredos descriptografados — uso interno (envio de e-mail, login Microsoft 365).</summary>
    Task<string?> SenhaSmtpAsync();
    Task<string?> SegredoM365Async();

    Task SalvarPoliticaSenhaAsync(PoliticaSenha p, string usuario);

    /// <summary><paramref name="novaSenhaSmtp"/> vazia = mantém a atual.</summary>
    Task SalvarEmailAsync(ConfigEmail c, string? novaSenhaSmtp, string usuario);

    /// <summary><paramref name="novoSegredo"/> vazio = mantém a chave atual (troca registra data e autor).</summary>
    Task SalvarM365Async(bool loginHabilitado, string? tenantId, string? clientId, string? novoSegredo, DateTime? segredoExpiraEm, string usuario);
}

public class ConfiguracaoService : IConfiguracaoService
{
    // Chaves gravadas na tabela Configuracoes
    private const string KValidadeSenha = "Senha.ValidadeDias";
    private const string KEmailMetodo = "Email.Metodo", KRemetente = "Email.Remetente", KNomeRemetente = "Email.NomeRemetente";
    private const string KSmtpServidor = "Smtp.Servidor", KSmtpPorta = "Smtp.Porta", KSmtpSsl = "Smtp.Ssl",
                         KSmtpUsuario = "Smtp.Usuario", KSmtpSenha = "Smtp.Senha";
    private const string KM365Login = "M365.LoginHabilitado", KM365Tenant = "M365.TenantId", KM365Client = "M365.ClientId",
                         KM365Segredo = "M365.Segredo", KM365Expira = "M365.SegredoExpiraEm";

    private readonly AppDbContext _db;
    private readonly IDataProtector _protetor;

    public ConfiguracaoService(AppDbContext db, IDataProtectionProvider dp)
    {
        _db = db;
        _protetor = dp.CreateProtector("RDSMonitor.Configuracoes.Segredos");
    }

    private Task<Dictionary<string, Configuracao>> TodasAsync() =>
        _db.Configuracoes.AsNoTracking().ToDictionaryAsync(c => c.Chave);

    private static string? V(Dictionary<string, Configuracao> d, string k) =>
        d.TryGetValue(k, out var c) && !string.IsNullOrWhiteSpace(c.Valor) ? c.Valor : null;

    public async Task<PoliticaSenha> PoliticaSenhaAsync()
    {
        var d = await TodasAsync();
        return new(int.TryParse(V(d, KValidadeSenha), out var dias) && dias > 0 ? dias : 90);
    }

    public async Task<ConfigEmail> EmailAsync()
    {
        var d = await TodasAsync();
        return new(
            Enum.TryParse<MetodoEmail>(V(d, KEmailMetodo), out var m) ? m : MetodoEmail.Desligado,
            V(d, KRemetente), V(d, KNomeRemetente) ?? "RDSManager",
            V(d, KSmtpServidor), int.TryParse(V(d, KSmtpPorta), out var porta) ? porta : 587,
            V(d, KSmtpSsl) != "false", V(d, KSmtpUsuario), V(d, KSmtpSenha) is not null);
    }

    public async Task<ConfigM365> M365Async()
    {
        var d = await TodasAsync();
        d.TryGetValue(KM365Segredo, out var segredo);
        return new(
            V(d, KM365Login) == "true",
            V(d, KM365Tenant), V(d, KM365Client), !string.IsNullOrWhiteSpace(segredo?.Valor),
            DateTime.TryParse(V(d, KM365Expira), CultureInfo.InvariantCulture, DateTimeStyles.None, out var exp) ? exp : null,
            segredo?.Valor is null ? null : segredo.AlteradoEm, segredo?.AlteradoPor);
    }

    public async Task<string?> SenhaSmtpAsync() => Abrir(V(await TodasAsync(), KSmtpSenha));
    public async Task<string?> SegredoM365Async() => Abrir(V(await TodasAsync(), KM365Segredo));

    private string? Abrir(string? cifrado)
    {
        if (cifrado is null) return null;
        try { return _protetor.Unprotect(cifrado); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }   // chaves de criptografia trocadas
    }

    public async Task SalvarPoliticaSenhaAsync(PoliticaSenha p, string usuario)
    {
        await GravarAsync(usuario, (KValidadeSenha, Math.Clamp(p.ValidadeDias, 1, 3650).ToString()));
    }

    public async Task SalvarEmailAsync(ConfigEmail c, string? novaSenhaSmtp, string usuario)
    {
        var valores = new List<(string, string?)>
        {
            (KEmailMetodo, c.Metodo.ToString()), (KRemetente, Limpo(c.Remetente)), (KNomeRemetente, Limpo(c.NomeRemetente)),
            (KSmtpServidor, Limpo(c.SmtpServidor)), (KSmtpPorta, c.SmtpPorta.ToString()),
            (KSmtpSsl, c.SmtpSsl ? "true" : "false"), (KSmtpUsuario, Limpo(c.SmtpUsuario)),
        };
        if (!string.IsNullOrEmpty(novaSenhaSmtp)) valores.Add((KSmtpSenha, _protetor.Protect(novaSenhaSmtp)));
        await GravarAsync(usuario, valores.ToArray());
    }

    public async Task SalvarM365Async(bool loginHabilitado, string? tenantId, string? clientId, string? novoSegredo,
        DateTime? segredoExpiraEm, string usuario)
    {
        var valores = new List<(string, string?)>
        {
            (KM365Login, loginHabilitado ? "true" : "false"),
            (KM365Tenant, Limpo(tenantId)), (KM365Client, Limpo(clientId)),
            (KM365Expira, segredoExpiraEm?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrWhiteSpace(novoSegredo)) valores.Add((KM365Segredo, _protetor.Protect(novoSegredo.Trim())));
        await GravarAsync(usuario, valores.ToArray());
    }

    private async Task GravarAsync(string usuario, params (string Chave, string? Valor)[] valores)
    {
        foreach (var (chave, valor) in valores)
        {
            var c = await _db.Configuracoes.FindAsync(chave);
            if (c is null) _db.Configuracoes.Add(c = new Configuracao { Chave = chave });
            if (c.Valor == valor) continue;
            c.Valor = valor;
            c.AlteradoEm = DateTime.UtcNow;
            c.AlteradoPor = usuario;
        }
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private static string? Limpo(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
