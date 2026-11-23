using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Elarion.EntityFrameworkCore;

/// <summary>
/// Makes EF Core refuse a query that still invokes an Elarion rule (<c>Rule&lt;…&gt;.Invoke</c> or the
/// <c>Expression&lt;Func&lt;…&gt;&gt;.Invoke</c> marker) because <c>Expand()</c> was not called on it (ADR-0102).
/// </summary>
/// <remarks>
/// Without the check EF Core fails on such a marker in a filter, but evaluates one in the final projection on the
/// client, against an entity whose navigations were never loaded, so a rule over related rows answers silently wrong.
/// With it every unexpanded marker fails when EF Core compiles the query, naming the expression and the fix. The check
/// runs once per query shape (EF Core caches the compiled query), not per execution. A projection that should run a
/// rule in memory on purpose ends the query first (<c>.AsAsyncEnumerable()</c>, <c>.ToListAsync()</c>) and invokes the
/// rule on the loaded entities.
/// </remarks>
/// <example>
/// <code>
/// builder.Services.AddDbContext&lt;AppDbContext&gt;(options =&gt; options
///     .UseNpgsql(connectionString)
///     .UseElarionRuleCheck());
/// </code>
/// </example>
public static class RuleCheckDbContextOptionsExtensions {
    /// <summary>Refuses every query that still invokes an Elarion rule because <c>Expand()</c> was not called.</summary>
    public static DbContextOptionsBuilder UseElarionRuleCheck(this DbContextOptionsBuilder optionsBuilder) {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        return optionsBuilder.AddInterceptors(UnexpandedRuleInterceptor.Instance);
    }

    /// <inheritdoc cref="UseElarionRuleCheck(DbContextOptionsBuilder)"/>
    public static DbContextOptionsBuilder<TContext> UseElarionRuleCheck<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        return optionsBuilder.AddInterceptors(UnexpandedRuleInterceptor.Instance);
    }
}

/// <summary>
/// The query-compilation check behind <see cref="RuleCheckDbContextOptionsExtensions.UseElarionRuleCheck"/>. It runs
/// after EF Core has turned captured values into parameters, so it recognizes a rule invocation by its method alone:
/// this package does not reference the core package that defines the rules, so the methods are matched by name.
/// </summary>
internal sealed class UnexpandedRuleInterceptor : IQueryExpressionInterceptor {
    public static readonly UnexpandedRuleInterceptor Instance = new();

    private const string RulesNamespace = "Elarion.Expressions";

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData) {
        new Finder().Visit(queryExpression);
        return queryExpression;
    }

    private sealed class Finder : ExpressionVisitor {
        protected override Expression VisitMethodCall(MethodCallExpression node) {
            if (IsRuleInvocation(node.Method))
                throw new InvalidOperationException(
                    $"The query invokes a rule that was not expanded: '{node}'. A query provider cannot translate the "
                    + "Invoke marker, and in a final projection EF Core would run the rule on the client against an "
                    + "entity whose navigations were not loaded. Call Expand() on the query (or on the expression) after "
                    + "the last operator that uses a rule, or end the query and invoke the rule on the loaded entities.");

            return base.VisitMethodCall(node);
        }

        private static bool IsRuleInvocation(System.Reflection.MethodInfo method) {
            if (method.Name != "Invoke" || method.DeclaringType is not { Namespace: RulesNamespace } declaringType)
                return false;

            if (declaringType.Name == "RuleExpressionExtensions") return true;

            // Rule`2 to Rule`5 (one to four inputs) and any arity added later: the prefix spares this package a list
            // of names to keep in step with the core package it cannot reference.
            return declaringType.IsGenericType && declaringType.Name.StartsWith("Rule`", StringComparison.Ordinal);
        }
    }
}
