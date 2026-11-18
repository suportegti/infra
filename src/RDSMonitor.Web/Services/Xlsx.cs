using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace RDSMonitor.Web.Services;

public enum XlsxTipo { Texto, Numero, DataHora, Duracao }

public sealed record XlsxColuna(string Titulo, XlsxTipo Tipo = XlsxTipo.Texto, double Largura = 18);

/// <summary>
/// Gera uma planilha Excel (.xlsx) simples, de uma aba, sem dependências externas:
/// cabeçalho em negrito, primeira linha congelada, autofiltro, datas e durações
/// como valores reais (ordenáveis/somáveis no Excel).
/// </summary>
/// <remarks>
/// Valores aceitos por célula: string, números, <see cref="DateTime"/> (já em
/// horário local), <see cref="TimeSpan"/> e null.
/// </remarks>
public static class Xlsx
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // Índices de cellXfs em styles.xml
    private const int EstiloCabecalho = 1, EstiloDataHora = 2, EstiloDuracao = 3, EstiloTexto = 4;

    public static byte[] Gerar(string nomeAba, IReadOnlyList<XlsxColuna> colunas, IEnumerable<object?[]> linhas)
    {
        nomeAba = NomeAbaValido(nomeAba);
        var sheet = new StringBuilder();
        var ultimaCol = Coluna(colunas.Count - 1);

        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
             .Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">")
             .Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>")
             .Append("<cols>");
        for (var i = 0; i < colunas.Count; i++)
            sheet.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{colunas[i].Largura.ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
        sheet.Append("</cols><sheetData>");

        // Cabeçalho
        sheet.Append("<row r=\"1\">");
        for (var i = 0; i < colunas.Count; i++)
            Texto(sheet, $"{Coluna(i)}1", colunas[i].Titulo, EstiloCabecalho);
        sheet.Append("</row>");

        var r = 1;
        foreach (var linha in linhas)
        {
            r++;
            sheet.Append($"<row r=\"{r}\">");
            for (var i = 0; i < colunas.Count && i < linha.Length; i++)
            {
                var celula = $"{Coluna(i)}{r}";
                switch (linha[i])
                {
                    case null:
                        break;
                    case DateTime d:
                        Numero(sheet, celula, d.ToOADate(), EstiloDataHora);
                        break;
                    case TimeSpan t:
                        Numero(sheet, celula, Math.Max(0, t.TotalDays), EstiloDuracao);
                        break;
                    case int or long or double or decimal or float or short:
                        Numero(sheet, celula, Convert.ToDouble(linha[i], CultureInfo.InvariantCulture), 0);
                        break;
                    default:
                        Texto(sheet, celula, Convert.ToString(linha[i], CultureInfo.CurrentCulture) ?? "", EstiloTexto);
                        break;
                }
            }
            sheet.Append("</row>");
        }

        var area = $"A1:{ultimaCol}{Math.Max(r, 1)}";
        sheet.Append("</sheetData>")
             .Append($"<autoFilter ref=\"{area}\"/>")
             .Append("</worksheet>");

        var workbook =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            $"<sheets><sheet name=\"{Esc(nomeAba)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
            $"<definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">'{Esc(nomeAba.Replace("'", "''"))}'!$A$1:${ultimaCol}${Math.Max(r, 1)}</definedName></definedNames>" +
            "</workbook>";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entrada(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "</Types>");
            Entrada(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            Entrada(zip, "xl/workbook.xml", workbook);
            Entrada(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "</Relationships>");
            Entrada(zip, "xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy hh:mm:ss\"/><numFmt numFmtId=\"165\" formatCode=\"[h]:mm\"/></numFmts>" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F3A5F\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"5\">" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
                "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                "</cellXfs>" +
                "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                "</styleSheet>");
            Entrada(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return ms.ToArray();
    }

    private static void Texto(StringBuilder sb, string celula, string valor, int estilo) =>
        sb.Append($"<c r=\"{celula}\" t=\"inlineStr\" s=\"{estilo}\"><is><t xml:space=\"preserve\">{Esc(valor)}</t></is></c>");

    private static void Numero(StringBuilder sb, string celula, double valor, int estilo) =>
        sb.Append($"<c r=\"{celula}\"{(estilo > 0 ? $" s=\"{estilo}\"" : "")}><v>{valor.ToString("R", CultureInfo.InvariantCulture)}</v></c>");

    private static void Entrada(ZipArchive zip, string nome, string conteudo)
    {
        using var w = new StreamWriter(zip.CreateEntry(nome, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(conteudo);
    }

    /// <summary>Índice 0-based → letra da coluna (0=A, 26=AA).</summary>
    private static string Coluna(int i)
    {
        var s = string.Empty;
        for (i++; i > 0; i = (i - 1) / 26)
            s = (char)('A' + (i - 1) % 26) + s;
        return s;
    }

    /// <summary>Escapa para XML e remove caracteres de controle que o Excel rejeita.</summary>
    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\t' or '\n' or '\r': sb.Append(ch); break;
                case < ' ': break;
                default:
                    if (char.IsSurrogate(ch) || ch < '￾') sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string NomeAbaValido(string nome)
    {
        var limpo = new string(nome.Where(c => !"[]:*?/\\".Contains(c)).ToArray()).Trim('\'');
        if (limpo.Length == 0) limpo = "Planilha";
        return limpo.Length > 31 ? limpo[..31] : limpo;
    }
}
