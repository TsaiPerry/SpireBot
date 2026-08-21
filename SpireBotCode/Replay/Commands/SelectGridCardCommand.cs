using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace SpireBot.Replay.Commands;

/// <summary>
/// Select one or more cards from a grid selection screen (NCardGridSelectionScreen).
///
/// Recorded as: "SelectGridCard {idx0} {idx1} ..."
/// </summary>
public class SelectGridCardCommand : ReplayCommand
{
    private const string Prefix = "SelectGridCard ";

    public int[] Indices { get; }

    public override bool IsSelectionCommand => true;

    public SelectGridCardCommand(int[] indices) : base("")
    {
        Indices = indices;
    }

    public override string ToString()
        => $"{Prefix}{string.Join(" ", Indices)}";

    public override string Describe()
    {
        string idxStr = Indices.Length > 0 ? string.Join(", ", Indices) : "(none)";
        return $"select grid card indices=[{idxStr}]";
    }

    public override ExecuteResult Execute()
    {
        var screen = CardGridScreenCapture.ActiveScreen;
        if (screen == null)
            return ExecuteResult.Retry(300);

        var cards = CardGridScreenCapture.GetSelectableCards(screen);
        if (cards == null)
            return ExecuteResult.Retry(300);

        var selected = new List<CardModel>();
        foreach (int idx in Indices)
        {
            if (idx < 0 || idx >= cards.Count)
                return ExecuteResult.Retry(300);

            CardGridScreenCapture.ClickCard(screen, cards[idx]);
            selected.Add(cards[idx]);
        }

        CardGridScreenCapture.ConfirmSelection(screen, selected);
        // SpireBot delta 17: the game's own single-select confirm
        // (NDeckUpgradeSelectScreen.CheckIfSelectionComplete:248-257) pairs
        // SetResult with NOverlayStack.Instance.Remove(this). Resolving the
        // completion source without removing the screen leaves a ghost
        // overlay: the rest site re-offers its options underneath and the
        // bot smiths again (observed: 2 free upgrades minted at SXFY52G6VQ
        // act0 f12, rest never consumed). Mirror the full close.
        Godot.Callable.From(() =>
        {
            if (Godot.GodotObject.IsInstanceValid(screen) && screen.IsInsideTree())
                NOverlayStack.Instance?.Remove(screen);
        }).CallDeferred();
        CardGridScreenCapture.ActiveScreen = null;
        return ExecuteResult.Ok();
    }

    public static SelectGridCardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        return ParseIndices(raw, Prefix.Length);
    }

    private static SelectGridCardCommand? ParseIndices(string raw, int prefixLen)
    {
        string rest = raw.Substring(prefixLen).Trim();
        if (rest.Length == 0)
            return new SelectGridCardCommand(System.Array.Empty<int>());

        var parts = rest.Split(' ');
        var indices = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (int.TryParse(part, out int idx))
                indices.Add(idx);
            else
                return null;
        }
        return new SelectGridCardCommand(indices.ToArray());
    }
}
