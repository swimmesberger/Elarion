using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Elarion.Expressions;

/// <summary>
/// Replaces every rule invocation in a tree (<see cref="Rule{TInput, TResult}.Invoke"/>,
/// <see cref="Rule{TInput1, TInput2, TResult}.Invoke"/> and the <c>Invoke</c> extensions on expressions) with the
/// invoked body, its parameters substituted by the call's arguments.
/// </summary>
/// <remarks>
/// The invoked rule or expression is read out of the tree by walking constants and field or property accesses with
/// reflection, never by compiling the subtree: expansion stays usable without dynamic code and costs no compilation
/// per query.
/// </remarks>
internal sealed class RuleExpander : ExpressionVisitor {
    // The bodies being inlined, innermost last, to stop a rule that invokes itself instead of recursing forever.
    private readonly List<LambdaExpression> _inlining = [];

    protected override Expression VisitMethodCall(MethodCallExpression node) {
        if (!TryGetInvocation(node, out var lambda, out var arguments)) return base.VisitMethodCall(node);

        if (_inlining.Contains(lambda)) {
            throw new InvalidOperationException(
                $"Cannot expand '{node}': the invoked rule invokes itself, directly or through another rule, so it has "
                + "no finite expansion. Write the recursion as a query (a join or a recursive CTE) instead.");
        }

        var visitedArguments = new Expression[arguments.Count];
        for (var i = 0; i < arguments.Count; i++) visitedArguments[i] = Visit(arguments[i]);

        _inlining.Add(lambda);
        Expression body;
        try {
            body = Visit(lambda.Body);
        }
        finally {
            _inlining.RemoveAt(_inlining.Count - 1);
        }

        var inlined = ParameterReplacer.Replace(body, lambda.Parameters, visitedArguments);
        // A lambda's body may be of a type assignable to its return type (a string body of a Func<…, object>); the
        // call it replaces has the return type, which the parent node was built against.
        return inlined.Type == node.Type ? inlined : Expression.Convert(inlined, node.Type);
    }

    private static bool TryGetInvocation(
        MethodCallExpression node,
        [NotNullWhen(true)] out LambdaExpression? lambda,
        out IReadOnlyList<Expression> arguments) {
        lambda = null;
        arguments = [];
        var method = node.Method;
        if (method.Name != nameof(Rule<,>.Invoke)) return false;

        if (node.Object is not null && IsRuleType(method.DeclaringType)) {
            var rule = Evaluate(node.Object, node) as IRuleExpression
                       ?? throw new InvalidOperationException($"Cannot expand '{node}': the invoked rule is null.");
            lambda = rule.Lambda;
            arguments = node.Arguments;
            return true;
        }

        if (node.Object is null && method.DeclaringType == typeof(RuleExpressionExtensions)) {
            lambda = Evaluate(node.Arguments[0], node) as LambdaExpression
                     ?? throw new InvalidOperationException($"Cannot expand '{node}': the invoked expression is null.");
            arguments = node.Arguments.Skip(1).ToArray();
            return true;
        }

        return false;
    }

    private static bool IsRuleType(Type? type) {
        if (type is not { IsGenericType: true }) return false;
        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(Rule<,>) || definition == typeof(Rule<,,>);
    }

    /// <summary>
    /// Evaluates the expression that yields the invoked rule or expression: a constant, a static member, or a member
    /// of something evaluable (a captured local is a field of the compiler's closure constant).
    /// </summary>
    private static object? Evaluate(Expression expression, MethodCallExpression call) {
        switch (expression) {
            case ConstantExpression constant:
                return constant.Value;
            case MemberExpression member: {
                object? target = null;
                if (member.Expression is not null) {
                    target = Evaluate(member.Expression, call)
                             ?? throw new InvalidOperationException(
                                 $"Cannot expand '{call}': '{member.Expression}' is null.");
                }

                return member.Member switch {
                    FieldInfo field => field.GetValue(target),
                    PropertyInfo property => property.GetValue(target),
                    _ => throw NotEvaluable(expression, call),
                };
            }
            // An expression literal inside a tree (((Expression<Func<…>>)(x => …)).Invoke(y)) is a quoted lambda.
            case UnaryExpression { NodeType: ExpressionType.Quote } quote:
                return quote.Operand;
            // A reference conversion yields the same object; a value conversion would need to run, and no rule or
            // expression is a value type.
            case UnaryExpression {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs
            } conversion when !conversion.Type.IsValueType && !conversion.Operand.Type.IsValueType:
                return Evaluate(conversion.Operand, call);
            default:
                throw NotEvaluable(expression, call);
        }
    }

    private static NotSupportedException NotEvaluable(Expression expression, MethodCallExpression call) {
        return new NotSupportedException(
            $"Cannot expand '{call}': the invoked rule is reached through '{expression}', which Expand cannot evaluate "
            + "without compiling code. Reference the rule through a static field or property, or through a local "
            + "variable the lambda captures.");
    }
}

/// <summary>Replaces parameters of a lambda body with argument expressions.</summary>
internal sealed class ParameterReplacer : ExpressionVisitor {
    private readonly Dictionary<ParameterExpression, Expression> _replacements;

    private ParameterReplacer(Dictionary<ParameterExpression, Expression> replacements) {
        _replacements = replacements;
    }

    public static Expression Replace(
        Expression body,
        IReadOnlyList<ParameterExpression> parameters,
        IReadOnlyList<Expression> arguments) {
        var replacements = new Dictionary<ParameterExpression, Expression>(parameters.Count);
        for (var i = 0; i < parameters.Count; i++) {
            if (!ReferenceEquals(parameters[i], arguments[i])) replacements[parameters[i]] = arguments[i];
        }

        return replacements.Count == 0 ? body : new ParameterReplacer(replacements).Visit(body);
    }

    protected override Expression VisitParameter(ParameterExpression node) {
        return _replacements.TryGetValue(node, out var replacement) ? replacement : node;
    }
}
