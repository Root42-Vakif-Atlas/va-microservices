using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace VakifAtlas.Catalog.Api.Infrastructure;

// raw commands on the context's connection and current transaction (statements EF cannot express:
// relevance-ranked search, UPDATE … RETURNING, SKIP LOCKED leases)
internal static class SqlCommands
{
    public static async Task<List<T>> QueryAsync<T>(this DatabaseFacade database, string sql,
        IEnumerable<NpgsqlParameter> parameters, Func<DbDataReader, T> read, CancellationToken cancellationToken)
    {
        await database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = database.CurrentTransaction?.GetDbTransaction();
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter.Clone());
            }

            var rows = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(read(reader));
            }

            return rows;
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }
}
