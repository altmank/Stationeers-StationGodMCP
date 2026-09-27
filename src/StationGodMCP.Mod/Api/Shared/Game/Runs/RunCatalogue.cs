#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// A kit's one-cell pieces at each of the 24 axis rotations, as the ends they would have (read from the registered
/// prefab: its ends' transforms turned and put on the grid, PieceShapes.Placed), and the first piece and turn for
/// each set of ends (PieceCatalogue). Every choice is checked again where it is built: the prefab placed there must
/// have exactly the one cell and the ends wanted.
/// </summary>
internal sealed class RunCatalogue
{
    private readonly List<Structure> _pieces;
    private readonly PieceCatalogue _catalogue;

    private RunCatalogue(Kit kit, List<Structure> pieces, PieceCatalogue catalogue)
    {
        Kit = kit;
        _pieces = pieces;
        _catalogue = catalogue;
    }

    internal Kit Kit { get; }

    internal static RunCatalogue Of(Kit kit, GridCell reference)
    {
        List<Structure> pieces = new List<Structure>();
        List<PieceOption> options = new List<PieceOption>();
        Vector3 at = PieceShapes.CentreOf(reference);
        foreach (Structure piece in kit.Pieces)
        {
            bool offered = false;
            for (int rotation = 0; rotation < PieceShapes.AxisRotations.Length; rotation++)
            {
                PieceModel? model = PieceShapes.Placed(piece, at, PieceShapes.AxisRotations[rotation], 0);
                EndSet? ends = model != null ? OneCellEnds(model, reference) : null;
                if (ends.HasValue)
                {
                    options.Add(new PieceOption(pieces.Count, rotation, ends.Value, OutputOf(model!, reference)));
                    offered = true;
                }
            }

            if (offered)
            {
                pieces.Add(piece);
            }
        }

        return new RunCatalogue(kit, pieces, new PieceCatalogue(options));
    }

    /// <summary>
    /// The pieces with exactly the ends, one per direction their output end can point (a chute junction's two turns);
    /// a single entry for pieces without direction, none when the kit has no piece with the ends.
    /// </summary>
    internal List<RunChoice> Orientations(EndSet ends) => _catalogue.Orientations(ends).ConvertAll(ChoiceOf);

    private RunChoice ChoiceOf(PieceOption option) =>
        new RunChoice(_pieces[option.Piece], PieceShapes.AxisRotations[option.Rotation], option.Output);

    internal List<string> Shapes() => _catalogue.Shapes();

    /// <summary>
    /// The model of the choice standing in the cell under the id, when it is exactly the one cell with exactly the
    /// ends; null otherwise.
    /// </summary>
    internal static PieceModel? Verified(RunChoice choice, GridCell cell, EndSet ends, long id)
    {
        PieceModel? model = PieceShapes.Placed(choice.Prefab, PieceShapes.CentreOf(cell), choice.Rotation, id);
        return model != null && OneCellEnds(model, cell) is EndSet found && found.Equals(ends) ? model : null;
    }

    // The direction of the model's one end with an output role (ConnectionRole Output or Output2); null for none or two.
    private static GridStep? OutputOf(PieceModel model, GridCell cell)
    {
        GridStep? output = null;
        foreach (PieceEnd end in model.Ends)
        {
            if (!ChuteRoles.LetsOut(end.Role))
            {
                continue;
            }

            if (output.HasValue)
            {
                return null;
            }

            output = GridStep.Between(cell, end.Local);
        }

        return output;
    }

    // The ends of a piece that occupies exactly the cell and whose every end faces out of it to a neighbour.
    private static EndSet? OneCellEnds(PieceModel model, GridCell cell)
    {
        if (model.Cells.Count != 1 || !model.Cells[0].Equals(cell) || model.Ends.Count == 0)
        {
            return null;
        }

        EndSet ends = EndSet.AtCell(model, cell);
        return ends.Count == model.Ends.Count ? ends : null;
    }
}

/// <summary>A piece of a kit, how it is turned, and where its output end then points (null for no direction).</summary>
internal sealed class RunChoice
{
    internal RunChoice(Structure prefab, Quaternion rotation, GridStep? output)
    {
        Prefab = prefab;
        Rotation = rotation;
        Output = output;
    }

    internal Structure Prefab { get; }

    internal Quaternion Rotation { get; }

    internal GridStep? Output { get; }
}
