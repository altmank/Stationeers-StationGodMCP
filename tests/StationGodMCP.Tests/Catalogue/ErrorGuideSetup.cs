#nullable enable

using System.Runtime.CompilerServices;
using StationGodMCP.Pure.Catalogue;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The mod takes its error pointers from the verb document it loads at start; the tests take them from the same
/// document (the catalogue/verbs this assembly embeds) before any test runs, so every error view the tests write
/// carries its see as the mod's would.
/// </summary>
internal static class ErrorGuideSetup
{
    [ModuleInitializer]
    internal static void UseTheVerbCatalogue()
    {
        ErrorGuide.Use(VerbDocument.Embedded);
    }
}
