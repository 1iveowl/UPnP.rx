using System.ComponentModel;

namespace System.Runtime.CompilerServices;

/// <summary>
/// Lets this netstandard2.0 assembly use <c>init</c> accessors and positional records,
/// which the compiler emits a reference to this type for.
/// </summary>
/// <remarks>
/// netstandard2.0 has no such type, so without this shim a positional record struct fails
/// to compile with CS0518. Analyzer assemblies must target netstandard2.0 (that is what
/// Roslyn loads), so the shim is the standard answer rather than a workaround. Internal,
/// so it cannot collide with a consumer's own copy - and this assembly ships as an
/// analyzer, never as a reference.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
