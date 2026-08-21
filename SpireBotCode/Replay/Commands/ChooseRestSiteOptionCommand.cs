using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;

using SpireBot.Replay.Patches;
namespace SpireBot.Replay.Commands;

/// <summary>
/// Choose a rest site option (e.g. HEAL, SMITH).
/// Recorded as: "ChooseRestSiteOption {optionId}"
/// </summary>
public class ChooseRestSiteOptionCommand : ReplayCommand
{
    private const string Prefix = "ChooseRestSiteOption ";

    public string OptionId { get; }


    public ChooseRestSiteOptionCommand(string optionId) : base("")
    {
        OptionId = optionId;
    }

    public override string ToString() => $"ChooseRestSiteOption {OptionId}";

    public override string Describe() => $"choose rest site option '{OptionId}'";

    public override ExecuteResult Execute()
    {
        var sync = ReplayState.ActiveRestSiteSynchronizer;
        if (sync == null)
            return ExecuteResult.Retry(300);

        RestSiteOption? chosenOption = null;
        foreach (var option in sync.GetLocalOptions())
        {
            if (option.OptionId == OptionId)
            {
                chosenOption = option;
                break;
            }
        }

        if (chosenOption == null)
            return ExecuteResult.Retry(300);

        // SpireBot delta 17 (Fix C, third guard): ReplayDispatcher's rest-option enumeration
        // (ReplayDispatcher.cs:221-227) walks sync.GetLocalOptions() with no IsEnabled filter,
        // so the policy can name an option a real player could never click — e.g.
        // SmithRestSiteOption.IsEnabled => Deck.UpgradableCardCount != 0 on a fully-upgraded
        // deck, or Cook needing >=2 removable cards. The button-affordance gate below (Fix B)
        // does NOT catch this: NClickableControl.IsEnabled only reflects what
        // NRestSiteRoom.DisableOptions()/EnableOptions() drive, while a disabled OPTION instead
        // snapshots into NRestSiteButton._isUnclickable at NRestSiteButton.Create
        // (NRestSiteButton.cs:~107) and is checked by OnRelease() itself (`if
        // (!_isUnclickable)`) — so ForceClick() below would reach OnRelease(), find
        // _isUnclickable true, and silently no-op: Execute() would still return Ok(), the room
        // would re-offer the same (still-disabled) option, and a live policy could loop on it
        // forever. Check the option's own IsEnabled first and refuse before ever touching the
        // button, same idiom as the CheckOrRefuse refusal below and delta 16's chest gate.
        if (!chosenOption.IsEnabled)
        {
            PlayerActionBuffer.LogDispatcher(
                $"[ChooseRestSiteOptionCommand] refusing '{OptionId}' — RestSiteOption.IsEnabled " +
                "is false (e.g. Smith with no upgradable cards, Cook with <2 removable); a player " +
                "could not click it either.");
            return ExecuteResult.Ok();
        }

        // SpireBot delta 17 (Fix B, defense-in-depth for the SMITH ghost-overlay loop — see
        // SelectGridCardCommand.cs and VENDORED-FROM.md delta 17): drive the rest site's own
        // NRestSiteButton click path instead of calling RestSiteSynchronizer.ChooseLocalOption
        // directly. NRestSiteButton.SelectOption (NRestSiteButton.cs:139-171) is the ONLY code
        // that calls NRestSiteRoom.DisableOptions()/EnableOptions(), and it only runs from the
        // button's OnRelease() override (:130-137) — calling the synchronizer directly, as this
        // command previously did, never disables the button, so a still-"live" button could be
        // re-dispatched onto mid-resolution behind a ghost overlay with nothing to refuse it.
        // ForceClick() (NClickableControl.cs:255-259, "Used by AutoSlay for automated testing")
        // is required rather than a raw EmitSignal(Released, ...) the way delta 16's chest/
        // proceed-button drives work: those buttons are wired via an external
        // .Connect(SignalName.Released, ...) subscriber, but NRestSiteButton never Connects one
        // — SelectOption is only reachable from OnRelease() itself, which only a real click or
        // ForceClick() invokes (EmitSignal(Released, ...) alone fires the signal with no
        // listener and does nothing here). ForceClick() calls OnRelease() synchronously, which
        // calls TaskHelper.RunSafely(SelectOption(Option)) — the async SelectOption's body runs
        // synchronously up to its first await, and DisableOptions() sits before that await
        // (:146-147), so by the time ForceClick() returns here every rest-site button (this one
        // included) is already IsEnabled == false. SelectOption also performs the same
        // RestSiteSynchronizer.ChooseLocalOption(...) call this command used to make directly,
        // so the multiplayer message-send path is identical — only the gating around it changes.
        var button = NRestSiteRoom.Instance?.GetButtonForOption(chosenOption);
        if (!SpireBot.SpireBotCode.Affordance.CheckOrRefuse(button, $"ChooseRestSiteOption {OptionId}"))
        {
            PlayerActionBuffer.LogDispatcher(
                $"[ChooseRestSiteOptionCommand] refusing '{OptionId}' — its button is not live " +
                "(already chosen / mid-resolution / gone); a player could not click it either.");
            return ExecuteResult.Ok();
        }

        PlayerActionBuffer.LogDispatcher($"Dispatcher path notifying rest site: ForceClick '{OptionId}'");
        button!.ForceClick();
        return ExecuteResult.Ok();
    }

    public static ChooseRestSiteOptionCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        string optionId = raw.Substring(Prefix.Length);
        return new ChooseRestSiteOptionCommand(optionId);
    }
}
