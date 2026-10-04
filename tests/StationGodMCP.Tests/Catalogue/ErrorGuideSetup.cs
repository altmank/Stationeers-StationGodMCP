#nullable enable

using System.IO;
using System.Runtime.CompilerServices;
using StationGodMCP.Pure.Catalogue;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The mod takes its error pointers from the catalogue it loads at start; the tests take them from the assembled
/// catalogue.json before any test runs, so every error view the tests write carries its see as the mod's would.
/// </summary>
internal static class ErrorGuideSetup
{
    [ModuleInitializer]
    internal static void UseTheAssembledCatalogue()
    {
        try
        {
            ErrorGuide.Use(Catalogue.Load(File.ReadAllText(CatalogueFiles.AssembledPath)));
        }
        catch (CatalogueException)
        {
            // CatalogueConsistencyTests names the problem; without pointers the error views carry no see.
        }
    }
}
