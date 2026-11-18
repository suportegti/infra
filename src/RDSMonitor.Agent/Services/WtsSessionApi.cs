using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// Acesso às sessões do Remote Desktop pela API nativa do Windows (wtsapi32).
/// Diferente do <c>quser</c>, não depende do idioma do Windows nem do formato
/// de data, e enxerga as sessões desconectadas (que no quser aparecem sem
/// SESSIONNAME e com o estado traduzido).
/// </summary>
internal static class WtsSessionApi
{
    private static readonly IntPtr ServidorLocal = IntPtr.Zero;   // WTS_CURRENT_SERVER_HANDLE

    // WTS_INFO_CLASS
    private const int WTSUserName    = 5;
    private const int WTSDomainName  = 7;
    private const int WTSSessionInfo = 24;

    /// <summary>WTS_CONNECTSTATE_CLASS.</summary>
    public enum EstadoWts
    {
        Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init,
    }

    public sealed record SessaoWts(
        int       SessionId,
        string    Usuario,
        string?   Dominio,
        string?   NomeEstacao,
        EstadoWts Estado,
        DateTime? LogonUtc,
        int       OciosidadeMinutos);

    // ── Enumeração ───────────────────────────────────────────────────────────

    /// <summary>Todas as sessões do servidor que têm um usuário associado.</summary>
    public static List<SessaoWts> ListarSessoesComUsuario()
    {
        if (!WTSEnumerateSessionsW(ServidorLocal, 0, 1, out var buffer, out var total))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSEnumerateSessions falhou");

        var resultado = new List<SessaoWts>();
        try
        {
            var tamanho = Marshal.SizeOf<WTS_SESSION_INFOW>();
            for (var i = 0; i < total; i++)
            {
                var info   = Marshal.PtrToStructure<WTS_SESSION_INFOW>(buffer + i * tamanho);
                var estado = (EstadoWts)info.State;
                if (estado is EstadoWts.Listen or EstadoWts.Down or EstadoWts.Init or EstadoWts.Reset)
                    continue;

                var usuario = ConsultarTexto(info.SessionId, WTSUserName);
                if (string.IsNullOrWhiteSpace(usuario)) continue;   // tela de logon, serviços (sessão 0)

                var (logon, ociosidade) = ConsultarTempos(info.SessionId, estado);

                resultado.Add(new SessaoWts(
                    info.SessionId,
                    usuario,
                    ConsultarTexto(info.SessionId, WTSDomainName),
                    string.IsNullOrWhiteSpace(info.pWinStationName) ? null : info.pWinStationName,
                    estado,
                    logon,
                    ociosidade));
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return resultado;
    }

    private static string? ConsultarTexto(int sessionId, int classe)
    {
        if (!WTSQuerySessionInformationW(ServidorLocal, sessionId, classe, out var buffer, out _))
            return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    /// <summary>
    /// Logon (UTC) e ociosidade em minutos. Para sessões desconectadas a
    /// ociosidade conta a partir da desconexão, como o quser.
    /// </summary>
    private static (DateTime? Logon, int Ociosidade) ConsultarTempos(int sessionId, EstadoWts estado)
    {
        if (!WTSQuerySessionInformationW(ServidorLocal, sessionId, WTSSessionInfo, out var buffer, out _))
            return (null, 0);
        try
        {
            var info  = Marshal.PtrToStructure<WTSINFOW>(buffer);
            var agora = info.CurrentTime > 0 ? info.CurrentTime : DateTime.UtcNow.ToFileTimeUtc();

            DateTime? logon = info.LogonTime > 0 ? DateTime.FromFileTimeUtc(info.LogonTime) : null;

            var referencia = estado == EstadoWts.Disconnected && info.DisconnectTime > 0
                ? info.DisconnectTime
                : info.LastInputTime;

            var ociosidade = referencia > 0 && agora > referencia
                ? (int)TimeSpan.FromTicks(agora - referencia).TotalMinutes
                : 0;

            return (logon, ociosidade);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    // ── Mensagem ─────────────────────────────────────────────────────────────

    private const int MB_OK              = 0x00000000;
    private const int MB_ICONINFORMATION = 0x00000040;
    private const int MB_SETFOREGROUND   = 0x00010000;
    private const int MB_TOPMOST         = 0x00040000;

    /// <summary>
    /// Mostra uma caixa de mensagem na sessão do usuário (sem esperar resposta).
    /// Funciona a partir do serviço (LocalSystem) para qualquer sessão.
    /// </summary>
    public static void EnviarMensagem(int sessionId, string titulo, string mensagem, int timeoutSegundos)
    {
        var ok = WTSSendMessageW(ServidorLocal, sessionId,
            titulo,   titulo.Length * 2,
            mensagem, mensagem.Length * 2,
            MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST,
            timeoutSegundos, out _, false);

        if (!ok)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTS_SESSION_INFOW
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pWinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTSINFOW
    {
        public int State;
        public int SessionId;
        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version,
        out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, int infoClass,
        out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSSendMessageW(IntPtr hServer, int sessionId,
        string pTitle, int titleLength, string pMessage, int messageLength,
        int style, int timeout, out int pResponse, bool bWait);
}
