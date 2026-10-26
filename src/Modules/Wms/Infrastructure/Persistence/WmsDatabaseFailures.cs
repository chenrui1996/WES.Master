using System.Data.Common;
using MySql.Data.MySqlClient;
using Wes.Wms.Features.Ledger;

namespace Wes.Wms.Infrastructure.Persistence;

internal static class WmsDatabaseFailures
{
    internal static WmsDatabaseException Safe(DbException error) =>
        new(error.IsTransient || error is MySqlException { Number: 1040 or 1042 or 1205 or 1213 or 2006 or 2013 });
}
