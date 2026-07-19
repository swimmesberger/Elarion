// Polyfill required for C# 9 record types (positional records use init-only properties)
// when targeting netstandard2.0, which pre-dates the System.Runtime.CompilerServices.IsExternalInit type.
// ReSharper disable once CheckNamespace

namespace System.Runtime.CompilerServices;

[ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)]
internal static class IsExternalInit {
}
