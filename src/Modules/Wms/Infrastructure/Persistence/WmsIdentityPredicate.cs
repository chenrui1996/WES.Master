using System.Linq.Expressions;

namespace Wes.Wms.Infrastructure.Persistence;

internal static class WmsIdentityPredicate
{
    // The MySQL provider cannot reliably translate primitive Guid collection Contains.
    internal static Expression<Func<TEntity, bool>> MatchesAnyId<TEntity>(IReadOnlyCollection<Guid> ids,
        Expression<Func<TEntity, Guid>> keySelector)
    {
        var body = ids.Select(id => (Expression)Expression.Equal(keySelector.Body, Expression.Constant(id)))
            .Aggregate((Expression)Expression.Constant(false), Expression.OrElse);
        return Expression.Lambda<Func<TEntity, bool>>(body, keySelector.Parameters);
    }
}
