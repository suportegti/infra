namespace RDSMonitor.Web.Data.Entities;

/// <summary>
/// Configuração do sistema (tela Configurações) guardada como chave/valor.
/// Segredos (senha do SMTP, chave do aplicativo Microsoft 365) são gravados
/// criptografados (ASP.NET Data Protection) — nunca em texto puro.
/// </summary>
public class Configuracao
{
    public string Chave { get; set; } = string.Empty;
    public string? Valor { get; set; }
    public DateTime AlteradoEm { get; set; } = DateTime.UtcNow;
    public string? AlteradoPor { get; set; }
}
