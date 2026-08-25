using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
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

    /// <summary>Two-phase execute: phase 1 clicks the cards (their highlight becomes
    /// visible) and returns Retry so the dispatcher re-runs this SAME instance after the
    /// hold; phase 2 confirms. Gives the user a beat to see WHICH card the bot picked —
    /// click+confirm+close used to land in one frame.</summary>
    private bool _clicked;

    private readonly List<CardModel> _selected = new();

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
            return _clicked ? ExecuteResult.Ok() : ExecuteResult.Retry(300);
        if (!Godot.GodotObject.IsInstanceValid(screen))
        {
            // Screen freed under us (room ended mid-hold): a clicked selection is moot,
            // consume; an unclicked one keeps the pre-existing retry-until-stale behavior.
            CardGridScreenCapture.ActiveScreen = null;
            return _clicked ? ExecuteResult.Ok() : ExecuteResult.Retry(300);
        }

        if (!_clicked)
        {
            var cards = CardGridScreenCapture.GetSelectableCards(screen);
            if (cards == null)
                return ExecuteResult.Retry(300);

            foreach (int idx in Indices)
            {
                if (idx < 0 || idx >= cards.Count)
                    return ExecuteResult.Retry(300);
            }
            foreach (int idx in Indices)
            {
                CardGridScreenCapture.ClickCard(screen, cards[idx]);
                _selected.Add(cards[idx]);
            }
            _clicked = true;

            // Hold with the pick highlighted so the user can see it, then confirm on the
            // retry pass. Skipped when the click auto-completed the screen (it already
            // closed) or at Instant speed.
            double hold = SpireBot.SpireBotCode.PacingPlan.SelectionHoldSeconds(
                SpireBot.SpireBotCode.SpireBotConfig.DecisionSpeed);
            if (hold > 0 && !CardGridScreenCapture.IsSelectionCompleted(screen))
                return ExecuteResult.Retry((int)(hold * 1000));
        }

        var selected = _selected;

        // NCombatPileCardSelectScreen (Neow's Fury / Seeker Strike retrieval) must close
        // through its OWN CompleteSelection (UnsubscribeFromPile → SetResult → Remove,
        // NCombatPileCardSelectScreen.cs:199-204). Resolving the completion source directly
        // leaves the screen subscribed to _pile.ContentsChanged; the resumed card play's
        // pile moves then re-enter UpdatePileContents on a completing screen and its plain
        // SetResult throws mid pile-move — observed as the played card's animation into
        // the discard pile freezing (TEM8SHH8KU act0 f8, Neow's Fury). ClickCard may
        // already have auto-completed the screen (CheckIfSelectionComplete when the
        // selection filled the grid), in which case the game closed it and nothing more
        // may touch it.
        if (screen is NCombatPileCardSelectScreen combatPile)
        {
            if (!CardGridScreenCapture.IsSelectionCompleted(screen)
                && !CardGridScreenCapture.CompleteCombatPileSelection(combatPile))
                CardGridScreenCapture.ConfirmSelection(screen, selected);
            CardGridScreenCapture.ActiveScreen = null;
            return ExecuteResult.Ok();
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
