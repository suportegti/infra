namespace RDSMonitor.Web.Data.Entities;

/// <summary>Funcionalidade do portal que disparou o e-mail. Só acrescente valores no fim (gravado como número).</summary>
public enum OrigemEmail
{
    /// <summary>Botão "Enviar e-mail de teste" em Configurações.</summary>
    Teste,
    /// <summary>"Esqueci minha senha" na tela de login.</summary>
    RecuperacaoSenha,
    /// <summary>Boas-vindas ao criar um usuário (link do portal, login e senha temporária).</summary>
    NovoUsuario,
}

/// <summary>
/// Registro de cada e-mail que o portal tentou enviar (tela Administração > E-mails enviados).
/// O CORPO não é gravado de propósito: ele pode conter senhas temporárias.
/// </summary>
public class EmailEnviado
{
    public long Id { get; set; }

    /// <summary>Momento do envio (UTC).</summary>
    public DateTime DataHora { get; set; } = DateTime.UtcNow;

    public OrigemEmail Origem { get; set; }

    public string Para { get; set; } = string.Empty;
    public string Assunto { get; set; } = string.Empty;

    /// <summary>"Smtp" ou "Microsoft365" (método configurado no momento do envio).</summary>
    public string? Metodo { get; set; }

    public bool Sucesso { get; set; }

    /// <summary>Motivo da falha devolvido pelo servidor SMTP / Microsoft 365.</summary>
    public string? Erro { get; set; }

    /// <summary>Usuário do portal a quem o e-mail se refere (ex.: conta criada ou que pediu a senha).</summary>
    public string? UsuarioRelacionado { get; set; }

    /// <summary>Quem disparou: o operador logado ou, no "Esqueci minha senha", o próprio usuário (anônimo).</summary>
    public string? DisparadoPor { get; set; }

    /// <summary>Endereço de origem da requisição que disparou o envio, quando conhecido.</summary>
    public string? Ip { get; set; }

    public static string Descrever(OrigemEmail o) => o switch
    {
        OrigemEmail.Teste            => "E-mail de teste",
        OrigemEmail.RecuperacaoSenha => "Recuperação de senha",
        OrigemEmail.NovoUsuario      => "Novo usuário",
        _                            => o.ToString(),
    };
}
