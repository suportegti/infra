namespace RDSMonitor.Web.Services;

/// <summary>
/// Ordenação de uma tabela por coluna (cabeçalho <c>ThOrdenavel</c>). Clicar na
/// mesma coluna inverte crescente/decrescente; uma coluna nova começa crescente,
/// exceto as de data/hora, que começam pela mais recente.
/// </summary>
public sealed class Ordenacao
{
    public string Campo { get; private set; }
    public bool   Desc  { get; private set; }

    public Ordenacao(string campo, bool desc = true)
    {
        Campo = campo;
        Desc  = desc;
    }

    public void Alternar(string campo, bool comecaDecrescente = false)
    {
        if (Campo == campo) Desc = !Desc;
        else
        {
            Campo = campo;
            Desc  = comecaDecrescente;
        }
    }

    /// <summary>Ordena pelos valores da coluna atual (texto sem diferenciar maiúsculas; vazios por último).</summary>
    public IEnumerable<T> Aplicar<T>(IEnumerable<T> itens, IReadOnlyDictionary<string, Func<T, object?>> colunas)
    {
        if (!colunas.TryGetValue(Campo, out var chave)) return itens;
        var comparador = Comparer<object?>.Create(Comparar);
        return Desc ? itens.OrderByDescending(chave, comparador) : itens.OrderBy(chave, comparador);
    }

    private int Comparar(object? a, object? b)
    {
        // Vazios sempre no fim, nos dois sentidos
        var aVazio = a is null || a is string { Length: 0 };
        var bVazio = b is null || b is string { Length: 0 };
        if (aVazio || bVazio) return aVazio == bVazio ? 0 : (aVazio ? 1 : -1) * (Desc ? -1 : 1);

        if (a is string sa && b is string sb)
            return string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase);
        return Comparer<object>.Default.Compare(a!, b!);
    }

    /// <summary>Para levar a ordenação da tela às exportações: "data:desc".</summary>
    public string ParaUrl() => $"{Campo}:{(Desc ? "desc" : "asc")}";

    public static Ordenacao DaUrl(string? valor, string campoPadrao, bool descPadrao = true)
    {
        var partes = (valor ?? string.Empty).Split(':', 2);
        return string.IsNullOrWhiteSpace(partes[0])
            ? new Ordenacao(campoPadrao, descPadrao)
            : new Ordenacao(partes[0], partes.Length < 2 || partes[1] != "asc");
    }
}
