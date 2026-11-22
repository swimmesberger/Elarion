namespace Elarion.Abstractions;

/// <summary>
/// The upload limits the current request declares with <see cref="UploadLimitsAttribute"/>, collected by the handler
/// pipeline before the handler runs and applied by the scoped <see cref="IUploadedFiles"/> to every read and keep of
/// those files. An <see cref="IUploadedFiles"/> implementation that serves handlers with declared limits implements it
/// too; the pipeline refuses to run such a handler without one (fail closed).
/// </summary>
/// <remarks>
/// Declarations are keyed by the file's <see cref="UploadedFile.Id"/> and only ever tighten: a file declared more than
/// once in a scope (two members, or a nested call) must satisfy every declaration, and constraints passed to
/// <see cref="IUploadedFiles"/> apply on top. Application code does not call it.
/// </remarks>
public interface IDeclaredUploadConstraints {
    /// <summary>Declares <paramref name="constraints"/> for <paramref name="file"/> in the current scope.</summary>
    /// <param name="file">A file of the current request, or <see langword="null"/> for an empty optional member.</param>
    /// <param name="constraints">The limits its member declares.</param>
    void Declare(UploadedFile? file, UploadConstraints constraints);
}
