namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Marks a handler as publicly accessible: authorization is skipped entirely. Wins over any
/// <c>Require*</c> attribute and exempts the handler from a default-authorization policy declared via
/// <see cref="ElarionAuthorizationDefaultsAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class AllowAnonymousAttribute : Attribute;
