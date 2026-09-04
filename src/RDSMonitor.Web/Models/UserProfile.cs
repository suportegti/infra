namespace RDSMonitor.Web.Models;

/// <summary>
/// Perfis de acesso ao painel, conforme definido no escopo do projeto:
/// Administrador (ações administrativas), Operador (somente visualização)
/// e Auditor (somente consulta a relatórios/histórico).
/// </summary>
public enum UserProfile
{
    Administrador,
    Operador,
    Auditor
}
