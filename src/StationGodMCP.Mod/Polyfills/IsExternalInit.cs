// netstandard2.1 has no IsExternalInit, which the compiler needs for init accessors and positional records
// (KeyFacts). The mod project only: the test project targets net8.0 and has the real one.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
