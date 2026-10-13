#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices;

/// <summary>Lets records and init-only properties compile for netstandard2.1, which lacks the marker type.</summary>
internal static class IsExternalInit
{
}
#endif
