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
/// Colunas novas em tabelas já existentes são acrescentadas com
/// ALTER TABLE ADD COLUMN (NOT NULL recebe um DEFAULT neutro). Renomear ou
/// remover colunas não é suportado — para isso, adotar migrations do EF Core.
/// </summary>
public static class SchemaUpgrader
{
    public static async Task EnsureSchemaAsync(AppDbContext db, ILogger log)
    {
        var conn = db.Database.GetDbConnection();

        if (await db.Database.EnsureCreatedAsync())
        {
            log.LogInformation("Banco de dados criado.");
            if (conn.State != ConnectionState.Open) await conn.OpenAsync();
            await CriarRegrasAsync(conn);
            return;
        }

        if (conn.State != ConnectionState.Open) await conn.OpenAsync();

        var antes = await ListarTabelasAsync(conn);

        var comandos = db.Database.GenerateCreateScript()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TornarIdempotente)
            .Where(sql => sql is not null)
            .Select(sql => sql!)
            .ToList();

        // Ordem: tabelas novas → colunas novas nas tabelas existentes → índices
        // (um índice novo pode depender de uma coluna recém-criada).
        bool EhTabela(string sql) => sql.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase);

        foreach (var sql in comandos.Where(EhTabela))
            await ExecutarAsync(conn, sql);

        var novas = (await ListarTabelasAsync(conn)).Except(antes).ToList();
        if (novas.Count > 0)
            log.LogInformation("Schema atualizado. Tabelas criadas: {Tabelas}", string.Join(", ", novas));

        await AdicionarColunasFaltantesAsync(db, conn, log);

        foreach (var sql in comandos.Where(c => !EhTabela(c)))
            await ExecutarAsync(conn, sql);

        await CriarRegrasAsync(conn);
    }

    /// <summary>
    /// Regras garantidas pelo próprio banco. Usuários do portal nunca são
    /// excluídos, só desabilitados, para preservar o histórico (auditoria,
    /// logoffs). O gatilho barra qualquer DELETE, inclusive feito por fora do portal.
    /// </summary>
    private static Task CriarRegrasAsync(System.Data.Common.DbConnection conn) => ExecutarAsync(conn, """
        CREATE TRIGGER IF NOT EXISTS "TR_Users_SemExclusao"
        BEFORE DELETE ON "Users"
        BEGIN
            SELECT RAISE(ABORT, 'Usuarios do portal nao podem ser excluidos: desabilite o usuario para preservar o historico.');
        END
        """);

    private static async Task ExecutarAsync(System.Data.Common.DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task AdicionarColunasFaltantesAsync(AppDbContext db, System.Data.Common.DbConnection conn, ILogger log)
    {
        foreach (var entidade in db.Model.GetEntityTypes())
        {
            var tabela = entidade.GetTableName();
            if (tabela is null) continue;

            var existentes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info(\"{tabela}\")";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) existentes.Add(reader.GetString(1));
            }
            if (existentes.Count == 0) continue;

            var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(tabela, entidade.GetSchema());
            foreach (var prop in entidade.GetProperties())
            {
                var coluna = prop.GetColumnName(store);
                if (coluna is null || existentes.Contains(coluna)) continue;

                var tipo = prop.GetColumnType();
                var sql  = $"ALTER TABLE \"{tabela}\" ADD COLUMN \"{coluna}\" {tipo}";
                if (!prop.IsColumnNullable(store))
                    sql += " NOT NULL DEFAULT " + ValorPadrao(prop.GetDefaultValue(), tipo);

                await using var alter = conn.CreateCommand();
                alter.CommandText = sql;
                await alter.ExecuteNonQueryAsync();
                log.LogInformation("Schema atualizado. Coluna criada: {Tabela}.{Coluna}", tabela, coluna);
            }
        }
    }

    private static string ValorPadrao(object? padrao, string tipo) => padrao switch
    {
        string texto => "'" + texto.Replace("'", "''") + "'",
        bool b       => b ? "1" : "0",
        null when tipo.Equals("TEXT", StringComparison.OrdinalIgnoreCase) => "''",
        null         => "0",
        _            => Convert.ToString(padrao, System.Globalization.CultureInfo.InvariantCulture) ?? "0",
    };

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
