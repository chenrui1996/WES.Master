using System.Data.Common;
using MySql.Data.MySqlClient;

namespace Wes.Platform.Infrastructure.Persistence;

internal static class PlatformDatabaseFailures
{
    internal static bool IsTransient(DbException error) =>
        error.IsTransient || error is MySqlException { Number: 1040 or 1042 or 1205 or 1213 or 2006 or 2013 };
}
