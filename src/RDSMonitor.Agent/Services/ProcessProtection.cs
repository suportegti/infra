namespace RDSMonitor.Agent.Services;

/// <summary>
/// Define quais processos NÃO podem ser derrubados pelo portal. A regra é
/// aplicada duas vezes: na coleta (o portal esconde o botão) e de novo na
/// hora de executar o comando (defesa em profundidade).
/// </summary>
public static class ProcessProtection
{
    /// <summary>
    /// Processos do Windows presentes em toda sessão cujo encerramento derruba
    /// a sessão inteira ou o servidor (ex.: csrss → tela azul).
    /// </summary>
    private static readonly HashSet<string> NomesCriticos = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "Secure System",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "fontdrvhost", "LogonUI", "sihost", "userinit",
        "rdpinit", "taskhostw", "RuntimeBroker", "ctfmon",
    };

    /// <summary>
    /// Um processo só pode ser derrubado se pertencer a uma conta de usuário
    /// (SID de domínio/local "S-1-5-21-..." ou Entra ID "S-1-12-1-...").
    /// Contas de sistema (SYSTEM, LOCAL/NETWORK SERVICE, DWM-n, UMFD-n) e
    /// processos sem dono identificável ficam protegidos. Usar o SID evita
    /// depender do idioma do Windows ("SYSTEM" x "SISTEMA").
    /// </summary>
    public static bool IsProtected(string name, string? ownerSid, int sessionId)
    {
        if (sessionId == 0) return true;                 // serviços
        if (NomesCriticos.Contains(name)) return true;
        if (string.IsNullOrEmpty(ownerSid)) return true;

        var ehUsuario = ownerSid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
                     || ownerSid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase);
        return !ehUsuario;
    }
}
