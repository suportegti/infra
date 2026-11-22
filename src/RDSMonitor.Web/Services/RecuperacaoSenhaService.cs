using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RDSMonitor.Web.Data;
using RDSMonitor.Web.Data.Entities;
using RDSMonitor.Web.Models;

namespace RDSMonitor.Web.Services;

public interface IRecuperacaoSenhaService
{
    /// <summary>
    /// "Esqueci minha senha": envia por e-mail uma senha temporária (30 min). A
    /// resposta para a tela é sempre a mesma, exista ou não o usuário, para não
    /// revelar quais contas existem.
    /// </summary>
    Task SolicitarAsync(string loginOuEmail, string? ip, string enderecoPortal);
}

public class RecuperacaoSenhaService : IRecuperacaoSenhaService
{
    public static readonly TimeSpan Validade = TimeSpan.FromMinutes(30);

    /// <summary>Intervalo mínimo entre dois pedidos para a mesma conta (evita encher a caixa de e-mail).</summary>
    private static readonly TimeSpan IntervaloMinimo = TimeSpan.FromMinutes(2);

    private readonly AppDbContext _db;
    private readonly IEmailService _email;
    private readonly IAutenticacaoService _autenticacao;
    private readonly ILogger<RecuperacaoSenhaService> _log;
    private readonly PasswordHasher<AppUser> _hasher = new();

    public RecuperacaoSenhaService(AppDbContext db, IEmailService email, IAutenticacaoService autenticacao,
        ILogger<RecuperacaoSenhaService> log)
    {
        _db = db;
        _email = email;
        _autenticacao = autenticacao;
        _log = log;
    }

    public async Task SolicitarAsync(string loginOuEmail, string? ip, string enderecoPortal)
    {
        var termo = (loginOuEmail ?? string.Empty).Trim().ToLower();
        if (termo.Length == 0) return;

        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.Username.ToLower() == termo || (u.Email != null && u.Email.ToLower() == termo));

        if (user is null || !user.Ativo || user.TipoAutenticacao != TipoAutenticacao.Local || string.IsNullOrWhiteSpace(user.Email))
        {
            await _autenticacao.RegistrarEventoAsync(loginOuEmail!, ip, AcessoResultado.UsuarioDesconhecido,
                "recuperação de senha: conta inexistente, desabilitada, Microsoft 365 ou sem e-mail");
            return;
        }

        var agora = DateTime.UtcNow;
        if (user.SenhaTemporariaExpiraEm is { } exp && exp - Validade > agora - IntervaloMinimo)
            return;   // pedido repetido em menos de 2 min

        if (!await _email.HabilitadoAsync())
        {
            _log.LogWarning("Recuperação de senha pedida para {Usuario}, mas o envio de e-mail não está configurado.", user.Username);
            await _autenticacao.RegistrarEventoAsync(user.Username, ip, AcessoResultado.RecuperacaoSolicitada, "não enviada: e-mail não configurado");
            return;
        }

        var senha = GerarSenhaTemporaria();
        user.SenhaTemporariaHash     = _hasher.HashPassword(user, senha);
        user.SenhaTemporariaExpiraEm = agora + Validade;
        await _db.SaveChangesAsync();

        try
        {
            var nome = WebUtility.HtmlEncode(user.DisplayName);
            var html = $"""
                <div style="font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2937">
                  <p>Olá, {nome}.</p>
                  <p>Recebemos um pedido para recuperar o acesso ao <strong>RDSManager</strong> (usuário <strong>{WebUtility.HtmlEncode(user.Username)}</strong>).</p>
                  <p>Sua senha temporária é:</p>
                  <p style="font-size:20px;font-family:Consolas,monospace;letter-spacing:2px;background:#f3f4f6;padding:10px 14px;display:inline-block;border-radius:6px">{WebUtility.HtmlEncode(senha)}</p>
                  <p>Ela vale até <strong>{Horario.Fmt(user.SenhaTemporariaExpiraEm.Value, "dd/MM/yyyy HH:mm")}</strong> (horário de Brasília). Ao entrar com ela, você será levado a criar uma senha nova.</p>
                  <p><a href="{WebUtility.HtmlEncode(enderecoPortal)}/login">Acessar o RDSManager</a></p>
                  <p style="color:#6b7280;font-size:12px">Se não foi você, ignore este e-mail: sua senha atual continua valendo.</p>
                </div>
                """;
            await _email.EnviarAsync(user.Email!, "RDSManager — senha temporária", html,
                new ContextoEmail(OrigemEmail.RecuperacaoSenha, user.Username, "(próprio usuário — Esqueci minha senha)", ip));
            await _autenticacao.RegistrarEventoAsync(user.Username, ip, AcessoResultado.RecuperacaoSolicitada, $"enviada para {Mascarar(user.Email!)}");
        }
        catch (Exception ex)
        {
            // Sem e-mail entregue, a senha temporária não deve continuar valendo
            user.SenhaTemporariaHash     = null;
            user.SenhaTemporariaExpiraEm = null;
            await _db.SaveChangesAsync();
            _log.LogError(ex, "Falha ao enviar a senha temporária de {Usuario}.", user.Username);
            await _autenticacao.RegistrarEventoAsync(user.Username, ip, AcessoResultado.RecuperacaoSolicitada, "falha no envio do e-mail");
        }
    }

    /// <summary>12 caracteres sem os ambíguos (0/O, 1/l/I), com letra maiúscula, minúscula, número e símbolo.</summary>
    public static string GerarSenhaTemporaria()
    {
        const string mai = "ABCDEFGHJKLMNPQRSTUVWXYZ", min = "abcdefghijkmnpqrstuvwxyz", num = "23456789", sim = "@#$%&*+?";
        var todos = mai + min + num + sim;
        var c = new List<char>
        {
            mai[RandomNumberGenerator.GetInt32(mai.Length)], min[RandomNumberGenerator.GetInt32(min.Length)],
            num[RandomNumberGenerator.GetInt32(num.Length)], sim[RandomNumberGenerator.GetInt32(sim.Length)],
        };
        while (c.Count < 12) c.Add(todos[RandomNumberGenerator.GetInt32(todos.Length)]);
        return new string(c.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).ToArray());
    }

    /// <summary>joao.silva@empresa.com → jo***@empresa.com (para o log de acessos).</summary>
    private static string Mascarar(string email)
    {
        var i = email.IndexOf('@');
        return i <= 2 ? "***" + email[Math.Max(i, 0)..] : email[..2] + "***" + email[i..];
    }
}
