using System.Linq.Expressions;

namespace Wes.Platform.Features.Authorization;

/// <summary>Typed scope predicates preserve Guid column conversions on the reviewed MySQL provider.</summary>
internal static class ScopeExpressions
{
    internal static Expression<Func<T, bool>> MatchIds<T>(Expression<Func<T, Guid>> selector, IEnumerable<Guid> ids)
    {
        // Oracle MySQL EF 10 cannot reliably bind Guid primitive collections mapped to varchar.
        Expression body = Expression.Constant(false);
        foreach (var id in ids.Distinct())
            body = Expression.OrElse(body, Expression.Equal(selector.Body, Expression.Constant(id)));
        return Expression.Lambda<Func<T, bool>>(body, selector.Parameters);
    }
}
