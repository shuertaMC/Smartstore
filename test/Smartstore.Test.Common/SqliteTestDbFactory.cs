using System;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Smartstore.Data;
using Smartstore.Data.Providers;

namespace Smartstore.Test.Common;

public sealed class SqliteTestDbFactory : DbFactory
{
    private readonly SqliteConnection _connection;

    public SqliteTestDbFactory(SqliteConnection connection)
    {
        _connection = connection;
    }

    public override DbSystemType DbSystem => DbSystemType.Unknown;

    public override DbConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
        => throw new NotImplementedException();

    public override DbConnectionStringBuilder CreateConnectionStringBuilder(
        string server, string database, string userName, string password)
        => throw new NotImplementedException();

    public override DataProvider CreateDataProvider(DatabaseFacade database)
        => new TestDataProvider(database);

    public override TContext CreateDbContext<TContext>(string connectionString, int? commandTimeout = null)
        => throw new NotImplementedException();

    public override DbContextOptionsBuilder ConfigureDbContext(DbContextOptionsBuilder builder, string connectionString)
    {
        return builder.UseSqlite(_connection);
    }
}
