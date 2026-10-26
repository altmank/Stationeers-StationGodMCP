#nullable enable

using System.Collections.Generic;
using StationGodMCP.LatticeLink;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The panel under the crosshair for the ghost there (Unity's immediate GUI, drawn from GhostOverlay.OnGUI): a dark
/// card with the ghost's tint as its edge, the title (label, else piece) and a badge for where it stands (planned,
/// ready, started, waiting, warning, blocked, stale), the reason, then rows for the plan, who placed it, the next build
/// steps, the materials and tools still needed and its ports, and Lattice's warnings. Its facts (GhostCard) are read
/// from Lattice twice a second for the ghost shown, not every frame; on StationGod's own board the card has the board
/// record alone. Sized for the screen's height (1080 rows = 1) times [Ghosts] TooltipScale; kept on screen.
/// </summary>
internal sealed class GhostTooltip
{
    private const float Refresh = 0.5f;
    private const float BaseWidth = 420f;

    private static readonly Color Panel = new Color(0.055f, 0.07f, 0.095f, 0.95f);
    private static readonly Color Divider = new Color(1f, 1f, 1f, 0.09f);
    private static readonly Color Bright = new Color(0.96f, 0.97f, 0.99f);
    private static readonly Color Soft = new Color(0.66f, 0.71f, 0.78f);
    private static readonly Color Amber = new Color(1f, 0.74f, 0.32f);
    private static readonly Color Ink = new Color(0.04f, 0.05f, 0.07f);

    private GhostCard? _card;
    private (long Id, string Title, string? Label) _cardOf;
    private float _readAt = -1f;

    private float _scale;
    private GUIStyle _title = null!;
    private GUIStyle _sub = null!;
    private GUIStyle _badge = null!;
    private GUIStyle _key = null!;
    private GUIStyle _value = null!;
    private GUIStyle _note = null!;

    internal void Draw(GhostMark ghost)
    {
        GhostCard card = CardOf(ghost);
        float scale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 3f) * GhostSettings.TooltipScale;
        Styles(scale);

        float width = Mathf.Min(BaseWidth * scale, Screen.width - 16f);
        float height = Layout(card, ghost, new Rect(0f, 0f, width, 0f), scale, false);
        float gap = 44f * scale;
        float x = Mathf.Clamp(Screen.width / 2f - width / 2f, 8f, Screen.width - width - 8f);
        float y = Screen.height / 2f + gap;
        if (y + height > Screen.height - 8f)
        {
            // No room below the crosshair: above it.
            y = Mathf.Max(8f, Screen.height / 2f - gap - height);
        }

        Layout(card, ghost, new Rect(x, y, width, height), scale, true);
    }

    // The card, read again when another ghost is shown or the last read is half a second old.
    private GhostCard CardOf(GhostMark ghost)
    {
        (long, string, string?) shown = (ghost.BoardId, ghost.Title, ghost.Label);
        float now = Time.realtimeSinceStartup;
        if (_card != null && _cardOf == shown && now - _readAt < Refresh)
        {
            return _card;
        }

        GhostCard? card = null;
        if (ghost.BoardId != 0 && LatticePresence.Running)
        {
            card = LatticePresence.Card(ghost.BoardId, ghost.Title, ghost.Label);
        }

        card ??= new GhostCard(ghost.BoardId, ghost.Title, ghost.Label)
        {
            Standing = ghost.BoardId == 0 ? GhostStanding.Preview : GhostStanding.Planned
        };
        card.PlacedBy ??= string.IsNullOrEmpty(ghost.Owner) ? null : ghost.Owner;
        _card = card;
        _cardOf = shown;
        _readAt = now;
        return card;
    }

    // Lays the card out in panel (its height ignored while measuring); draws when draw. Returns the height used.
    private float Layout(GhostCard card, GhostMark ghost, Rect panel, float scale, bool draw)
    {
        float pad = 14f * scale;
        float edge = 5f * scale;
        float left = panel.x + edge + pad;
        float inner = panel.width - edge - pad * 2f;
        if (draw)
        {
            Fill(new Rect(panel.x + 3f * scale, panel.y + 4f * scale, panel.width, panel.height), new Color(0f, 0f, 0f, 0.35f));
            Fill(panel, Panel);
            Color tint = ghost.SolidColor;
            tint.a = 1f;
            Fill(new Rect(panel.x, panel.y, edge, panel.height), tint);
            Outline(panel, new Color(tint.r, tint.g, tint.b, 0.45f));
        }

        float y = panel.y + pad;

        // Header: the title, the badge at the right.
        (float Red, float Green, float Blue) shade = GhostCard.Colour(card.Standing);
        Color standing = new Color(shade.Red, shade.Green, shade.Blue);
        GUIContent badge = new GUIContent(GhostCard.Word(card.Standing));
        Vector2 badgeSize = _badge.CalcSize(badge);
        float titleWidth = inner - badgeSize.x - 10f * scale;
        y += Text(new Rect(left, y, titleWidth, 0f), card.Title, _title, Bright, draw);
        if (draw)
        {
            Rect pill = new Rect(left + inner - badgeSize.x, panel.y + pad + 2f * scale, badgeSize.x, badgeSize.y);
            Fill(pill, standing);
            GUI.Label(pill, badge, _badge);
        }

        string sub = card.Intent + (card.Label != null ? " · " + card.Piece : string.Empty) +
                     (card.Id != 0 ? $" · ghost {card.Id}" : string.Empty);
        y += 2f * scale + Text(new Rect(left, y + 2f * scale, inner, 0f), sub, _sub, Soft, draw);
        if (card.Reason != null)
        {
            y += 6f * scale;
            y += Text(new Rect(left, y, inner, 0f), GhostCard.Short(card.Reason, 220), _value, standing, draw);
        }

        // The rows.
        List<(string Key, string Value)> rows = new List<(string Key, string Value)>();
        if (card.Plan != null)
        {
            rows.Add(("PLAN", card.Plan));
        }

        if (card.PlacedBy != null)
        {
            rows.Add(("PLACED BY", card.PlacedBy));
        }

        if (card.Steps.Count > 0)
        {
            string steps = string.Join("\n", card.Steps);
            if (card.StepsLeft > card.Steps.Count)
            {
                steps += $"\n+{card.StepsLeft - card.Steps.Count} more steps";
            }

            rows.Add((card.StepsLeft == 1 ? "NEXT STEP" : $"NEXT ({card.StepsLeft})", steps));
        }

        if (card.Needed.Count > 0)
        {
            rows.Add(("NEEDS", string.Join("\n", card.NeededLines())));
        }

        if (card.Tools.Count > 0)
        {
            rows.Add(("TOOLS", string.Join(", ", card.Tools)));
        }

        if (card.Ports.Count > 0)
        {
            rows.Add(("PORTS", card.PortsLine()));
        }

        if (rows.Count > 0)
        {
            y += 10f * scale;
            if (draw)
            {
                Fill(new Rect(left, y, inner, Mathf.Max(1f, scale)), Divider);
            }

            y += 9f * scale;
            float keyWidth = 96f * scale;
            foreach ((string key, string value) in rows)
            {
                float valueHeight = Text(new Rect(left + keyWidth, y, inner - keyWidth, 0f), value, _value, Bright, draw);
                float keyHeight = Text(new Rect(left, y + 2f * scale, keyWidth, 0f), key, _key, Soft, draw);
                y += Mathf.Max(valueHeight, keyHeight) + 5f * scale;
            }
        }

        if (card.Warnings.Count > 0)
        {
            y += 6f * scale;
            for (int index = 0; index < card.Warnings.Count && index < GhostCard.ShownWarnings; index++)
            {
                y += Text(new Rect(left, y, inner, 0f), "! " + GhostCard.Short(card.Warnings[index], 160), _value, Amber,
                    draw) + 3f * scale;
            }

            if (card.Warnings.Count > GhostCard.ShownWarnings)
            {
                y += Text(new Rect(left, y, inner, 0f), $"and {card.Warnings.Count - GhostCard.ShownWarnings} more warnings",
                    _note, Amber, draw);
            }
        }

        if (card.Partial)
        {
            y += 4f * scale + Text(new Rect(left, y + 4f * scale, inner, 0f), "Lattice is still checking this ghost", _note,
                Soft, draw);
        }

        return y + pad - panel.y;
    }

    // One wrapped text block at rect's top left and width; its height. Drawn with a one-pixel shadow when draw.
    private static float Text(Rect rect, string text, GUIStyle style, Color colour, bool draw)
    {
        GUIContent content = new GUIContent(text);
        float height = style.CalcHeight(content, rect.width);
        if (draw)
        {
            Rect at = new Rect(rect.x, rect.y, rect.width, height);
            style.normal.textColor = new Color(0f, 0f, 0f, 0.6f);
            GUI.Label(new Rect(at.x + 1f, at.y + 1f, at.width, at.height), content, style);
            style.normal.textColor = colour;
            GUI.Label(at, content, style);
        }

        return height;
    }

    private static void Fill(Rect rect, Color colour)
    {
        Color before = GUI.color;
        GUI.color = colour;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = before;
    }

    private static void Outline(Rect rect, Color colour)
    {
        Fill(new Rect(rect.x, rect.y, rect.width, 1f), colour);
        Fill(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), colour);
        Fill(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), colour);
    }

    // The styles at this scale, made again when the scale changes (a new screen size or setting).
    private void Styles(float scale)
    {
        if (_title != null && Mathf.Approximately(scale, _scale))
        {
            return;
        }

        _scale = scale;
        _title = Style(21, FontStyle.Bold, scale);
        _sub = Style(13, FontStyle.Normal, scale);
        _key = Style(11, FontStyle.Bold, scale);
        _value = Style(15, FontStyle.Normal, scale);
        _note = Style(12, FontStyle.Italic, scale);
        _badge = Style(12, FontStyle.Bold, scale);
        _badge.wordWrap = false;
        _badge.normal.textColor = Ink;
        _badge.alignment = TextAnchor.MiddleCenter;
        _badge.padding = new RectOffset(Mathf.RoundToInt(9f * scale), Mathf.RoundToInt(9f * scale),
            Mathf.RoundToInt(3f * scale), Mathf.RoundToInt(3f * scale));
    }

    private static GUIStyle Style(int size, FontStyle font, float scale) => new GUIStyle(GUI.skin.label)
    {
        fontSize = Mathf.Max(9, Mathf.RoundToInt(size * scale)),
        fontStyle = font,
        wordWrap = true,
        richText = false,
        alignment = TextAnchor.UpperLeft,
        padding = new RectOffset(0, 0, 0, 0),
        margin = new RectOffset(0, 0, 0, 0)
    };
}
