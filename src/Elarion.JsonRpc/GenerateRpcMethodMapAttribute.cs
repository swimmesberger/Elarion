namespace Elarion.JsonRpc;

/// <summary>
/// Marks a partial class to have its <c>RegisterAll</c> method body generated
/// by <c>Elarion.Generators.RpcMethodMapGenerator</c>.
/// Apply to the partial class that declares the <c>RegisterAll</c> method stub.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GenerateRpcMethodMapAttribute : Attribute;
