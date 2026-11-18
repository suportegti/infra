namespace RDSMonitor.Web.Services;

/// <summary>
/// Fuso horário do portal: Brasília (America/Sao_Paulo, UTC-3), independente
/// do fuso configurado no servidor onde o portal está instalado.
///
/// O banco grava tudo em UTC. As telas e exportações convertem com
/// <see cref="Br(DateTime)"/>, e os filtros digitados pelo usuário (horário de
/// Brasília) voltam para UTC com <see cref="ParaUtc"/>.
/// </summary>
public static class Horario
{
    public static readonly TimeZoneInfo Brasilia = Carregar();

    private static TimeZoneInfo Carregar()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        // Brasil não tem horário de verão desde 2019: UTC-3 fixo como último recurso.
        return TimeZoneInfo.CreateCustomTimeZone("Brasilia", TimeSpan.FromHours(-3), "Brasília", "Brasília");
    }

    /// <summary>Agora, no horário de Brasília.</summary>
    public static DateTime Agora => Br(DateTime.UtcNow);

    /// <summary>Hoje (meia-noite), no horário de Brasília.</summary>
    public static DateTime Hoje => Agora.Date;

    /// <summary>Valor gravado no banco (UTC) → horário de Brasília.</summary>
    public static DateTime Br(DateTime utc)
        => DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Brasilia),
            DateTimeKind.Unspecified);

    public static DateTime? Br(DateTime? utc) => utc is { } d ? Br(d) : null;

    /// <summary>Horário de Brasília digitado em um filtro → UTC, no mesmo formato gravado no banco.</summary>
    public static DateTime? ParaUtc(DateTime? brasilia) => brasilia switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } d => DateTime.SpecifyKind(d, DateTimeKind.Unspecified),
        { } d => DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), Brasilia),
            DateTimeKind.Unspecified),
    };

    /// <summary>Formata um valor UTC do banco no horário de Brasília.</summary>
    public static string Fmt(DateTime utc, string formato = "dd/MM/yyyy HH:mm") => Br(utc).ToString(formato);

    public static string Fmt(DateTime? utc, string formato = "dd/MM/yyyy HH:mm", string vazio = "—")
        => utc is { } d ? Fmt(d, formato) : vazio;
}
