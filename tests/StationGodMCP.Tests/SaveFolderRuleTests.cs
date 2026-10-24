#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The save folder from the save file the game loaded: its folder, or the parent of a save subfolder.</summary>
public sealed class SaveFolderRuleTests
{
    private const string Saves = @"C:\Users\LU\Documents\My Games\Stationeers\saves";

    [Theory]
    [InlineData(Saves + @"\Venus_2\Venus_2.save", Saves + @"\Venus_2")]
    [InlineData(Saves + @"\Venus_2\autosave\Venus_2_auto_3.save", Saves + @"\Venus_2")]
    [InlineData(Saves + @"\Venus_2\QuickSave\q.save", Saves + @"\Venus_2")]
    [InlineData(Saves + @"\Venus_2\manualsave\m.save", Saves + @"\Venus_2")]
    [InlineData(Saves + @"\Venus_2\Backup\b.save", Saves + @"\Venus_2")]
    public void TheStationFolderIsTheSavesFolderOrTheParentOfASaveSubfolder(string save, string folder)
    {
        Assert.Equal(folder, SaveFolderRule.StationFolderOf(save));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Venus_2.save")]
    public void NoFolderIsNone(string? save) => Assert.Null(SaveFolderRule.StationFolderOf(save));
}
