namespace Elarion.Abstractions.Authorization;

/// <summary>Requires the current principal to be in the named role.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class RequireRoleAttribute(string role) : Attribute {
    /// <summary>The required role.</summary>
    public string Role { get; } = role;
}
