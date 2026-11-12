using System.Data;
using Microsoft.EntityFrameworkCore;

namespace RDSMonitor.Web.Data;

/// <summary>
/// Mantém o schema do SQLite atualizado sem migrations e SEM apagar dados.
///
/// <c>EnsureCreated</c> só cria o banco quando ele não existe; num banco já
/// existente, tabelas novas do modelo ficariam faltando. Aqui geramos o script
/// de criação do modelo atual e executamos cada CREATE TABLE / CREATE INDEX
/// com "IF NOT EXISTS" — o que já existe é mantido, o que falta é criado.
///
/// Limitação: colunas novas em tabelas JÁ existentes não são adicionadas.
/// Se isso for necessário no futuro, adotar migrations do EF Core.
/// </summary>
public static class SchemaUpgrader
{
    public static async Task EnsureSchemaAsync(AppDbContext db, ILogger log)
    {
        if (await db.Database.EnsureCreatedAsync())
        {
            log.LogInformation("Banco de dados criado.");
            return;
        }

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();

        var antes = await ListarTabelasAsync(conn);

        var comandos = db.Database.GenerateCreateScript()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TornarIdempotente)
            .Where(sql => sql is not null);

        foreach (var sql in comandos)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        var novas = (await ListarTabelasAsync(conn)).Except(antes).ToList();
        if (novas.Count > 0)
            log.LogInformation("Schema atualizado. Tabelas criadas: {Tabelas}", string.Join(", ", novas));
    }

    private static string? TornarIdempotente(string sql)
    {
        if (sql.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase))
            return "CREATE TABLE IF NOT EXISTS " + sql["CREATE TABLE ".Length..];
        if (sql.StartsWith("CREATE UNIQUE INDEX ", StringComparison.OrdinalIgnoreCase))
            return "CREATE UNIQUE INDEX IF NOT EXISTS " + sql["CREATE UNIQUE INDEX ".Length..];
        if (sql.StartsWith("CREATE INDEX ", StringComparison.OrdinalIgnoreCase))
            return "CREATE INDEX IF NOT EXISTS " + sql["CREATE INDEX ".Length..];
        return null;   // qualquer outra instrução é ignorada por segurança
    }

    private static async Task<HashSet<string>> ListarTabelasAsync(System.Data.Common.DbConnection conn)
    {
        var tabelas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tabelas.Add(reader.GetString(0));
        return tabelas;
    }
}
