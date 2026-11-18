using System.Globalization;

namespace RDSMonitor.Web.Models;

/// <summary>
/// Período de pesquisa (data + hora separadas) usado nas telas de Auditoria e
/// Relatórios. Os valores são strings no formato dos inputs HTML
/// (<c>yyyy-MM-dd</c> e <c>HH:mm</c>), em horário LOCAL do servidor do portal.
/// </summary>
/// <remarks>
/// Regras:
/// <list type="bullet">
///   <item>"De" sem hora começa às 00:00.</item>
///   <item>"Até" sem hora vai até 23:59:59 do dia; com hora, inclui o minuto inteiro.</item>
///   <item>"Até" só com hora usa a data do "De" (ou hoje), para pesquisar uma faixa de
///         horário de um dia sem repetir a data.</item>
/// </list>
/// </remarks>
public sealed class PeriodoFiltro
{
    private const string FmtData = "yyyy-MM-dd";
    private const string FmtHora = "HH:mm";

    public string? Rapido  { get; set; }
    public string? DeData  { get; set; }
    public string? DeHora  { get; set; }
    public string? AteData { get; set; }
    public string? AteHora { get; set; }

    public PeriodoFiltro(string? rapido = "7d")
    {
        if (rapido is not null) Aplicar(rapido);
    }

    public static readonly (string Codigo, string Nome)[] Rapidos =
    {
        ("1h", "Última hora"), ("hoje", "Hoje"), ("ontem", "Ontem"), ("7d", "7 dias"), ("30d", "30 dias"),
    };

    /// <summary>Preenche o período a partir de um atalho (1h, hoje, ontem, 7d, 30d, tudo).</summary>
    public void Aplicar(string rapido)
    {
        var agora = RDSMonitor.Web.Services.Horario.Agora;
        var hoje  = agora.Date;
        Rapido = rapido;
        AteData = AteHora = null;

        switch (rapido)
        {
            case "1h":
                var h = agora.AddHours(-1);
                DeData = h.ToString(FmtData, CultureInfo.InvariantCulture);
                DeHora = h.ToString(FmtHora, CultureInfo.InvariantCulture);
                break;
            case "hoje":
                Dia(hoje);
                break;
            case "ontem":
                Dia(hoje.AddDays(-1));
                AteData = DeData;
                AteHora = "23:59";
                break;
            case "30d":
                Dia(hoje.AddDays(-30));
                break;
            case "tudo":
                DeData = DeHora = null;
                break;
            default:
                Rapido = "7d";
                Dia(hoje.AddDays(-7));
                break;
        }

        void Dia(DateTime d)
        {
            DeData = d.ToString(FmtData, CultureInfo.InvariantCulture);
            DeHora = "00:00";
        }
    }

    public DateTime? DeLocal =>
        Data(DeData) is { } d ? d + (Hora(DeHora) ?? TimeSpan.Zero) : null;

    public DateTime? AteLocal
    {
        get
        {
            var hora = Hora(AteHora);
            var data = Data(AteData)
                       ?? (hora is null ? null : Data(DeData) ?? RDSMonitor.Web.Services.Horario.Hoje);
            if (data is null) return null;

            return hora is { } t
                ? data.Value + t + TimeSpan.FromSeconds(60) - TimeSpan.FromMilliseconds(1)   // minuto inteiro
                : data.Value.AddDays(1).AddMilliseconds(-1);                                 // dia inteiro
        }
    }

    /// <summary>Período invertido (Até antes de De).</summary>
    public bool Invalido => DeLocal is { } de && AteLocal is { } ate && ate < de;

    /// <summary>Texto curto do período, para resumo/nome de arquivo.</summary>
    public string Descricao => (DeLocal, AteLocal) switch
    {
        (null, null)       => "todo o período",
        ({ } de, null)     => $"a partir de {de:dd/MM/yyyy HH:mm}",
        (null, { } ate)    => $"até {ate:dd/MM/yyyy HH:mm}",
        ({ } de, { } ate)  => $"de {de:dd/MM/yyyy HH:mm} até {ate:dd/MM/yyyy HH:mm}",
    };

    /// <summary>Formato usado nos parâmetros de URL das exportações.</summary>
    public static string? ParaUrl(DateTime? d) =>
        d?.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static DateTime? Data(string? s) =>
        DateTime.TryParseExact(s, FmtData, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static TimeSpan? Hora(string? s) =>
        TimeSpan.TryParseExact(s, new[] { @"hh\:mm", @"hh\:mm\:ss", @"h\:mm" }, CultureInfo.InvariantCulture, out var t)
        && t < TimeSpan.FromDays(1) ? t : null;
}
