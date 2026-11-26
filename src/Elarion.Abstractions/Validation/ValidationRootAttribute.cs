namespace Elarion.Abstractions.Validation;

/// <summary>
/// Marks a type that is validated outside a handler request, through
/// <see cref="IRequestValidator.ValidateAsync"/> with the type itself, so the validation generator emits the same
/// constant-constructed validation metadata for it as for a handler request (ADR-0027).
/// </summary>
/// <remarks>
/// <para>
/// A handler request is a validation root by its handler; nothing marks it. A type that reaches the validator another
/// way, such as a row read from a file, has no handler, so this marker names it instead. The type joins the
/// validation metadata of the module whose namespace contains it, and that metadata is registered only while the
/// module is enabled: ask <see cref="IRequestValidator.HasMetadata"/> before relying on the validator, because the
/// validator answers "valid" for a type it holds no metadata for.
/// </para>
/// <para>
/// A marked type that carries validation attributes and lies outside every module's namespace is a build error
/// (<c>ELVAL004</c>); one in a compilation that does not reference <c>Elarion.Validation</c> raises the build
/// warning <c>ELVAL002</c>. A marked type without validation attributes gets no metadata, and a generic type is not
/// a root. Like the handler registrations, the generator runs only in an assembly marked
/// <c>[assembly: UseElarion]</c> or <c>[assembly: GenerateModuleHandlers]</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// namespace Shop.Catalog;
///
/// [ValidationRoot]
/// public sealed record ProductRow {
///     [StringLength(40, MinimumLength = 1)]
///     public required string Sku { get; init; }
/// }
///
/// if (!validator.HasMetadata(typeof(ProductRow)))
///     throw new InvalidOperationException("No validation metadata is registered for ProductRow.");
/// var errors = await validator.ValidateAsync(typeof(ProductRow), row, cancellationToken);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ValidationRootAttribute : Attribute;
