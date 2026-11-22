namespace Elarion.Abstractions.Modules;

/// <summary>
/// The constants one module exports to clients (<see cref="ClientConstantsAttribute"/>). The module's generated services
/// register one per module that declares any, so a disabled module exports nothing; the JSON-RPC schema export writes
/// them as its <c>constants</c> block. Nothing reads them at run time.
/// </summary>
public sealed record ModuleClientConstants {
    /// <summary>The module name (the <c>[AppModule]</c> name).</summary>
    public required string Module { get; init; }

    /// <summary>The exported members, in declaration order.</summary>
    public required IReadOnlyList<ClientConstant> Constants { get; init; }
}

/// <summary>One exported constant.</summary>
public sealed record ClientConstant {
    /// <summary>The exported name: the member name with its first letter lower-cased.</summary>
    public required string Name { get; init; }

    /// <summary>The member's declared type, which the export serializes the value as.</summary>
    public required Type Type { get; init; }

    /// <summary>Reads the member's value; called by the schema export only.</summary>
    public required Func<object?> GetValue { get; init; }
}
