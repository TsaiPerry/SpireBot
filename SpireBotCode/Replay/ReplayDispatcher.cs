using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpireBot.Replay.Commands;

using SpireBot.Replay.Patches;
using SpireBot.Replay.Patches.Record;
using SpireBot.Replay.Patches.Replay;
using SpireBot.Replay.Utils;
namespace SpireBot.Replay;

/// <summary>
/// Central controller for replay command execution.  Sits between game events
/// and command execution (which invokes game APIs).
///
/// This gives the mod control over pacing and pausing replay commands
/// independently of game event timing.
/// </summary>
public static class ReplayDispatcher
{
    private static readonly MegaCrit.Sts2.Core.Logging.Logger _logger =
        new MegaCrit.Sts2.Core.Logging.Logger("SpireBot", LogType.Generic);
    private static readonly PropertyInfo? RunStateProp =
        typeof(RunManager).GetProperty("State",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static AbstractRoom? GetCurrentRoom()
    {
        var state = RunStateProp?.GetValue(RunManager.Instance) as IRunState;
        return state?.CurrentRoom;
    }

    private static bool LocalPlayerHasPotions()
    {
        var state = RunStateProp?.GetValue(RunManager.Instance) as IRunState;
        if (state == null) return false;
        var player = state.Players.FirstOrDefault();
        return player != null && player.Potions.Any();
    }

    private static readonly HashSet<Type> ShopCommandTypes = new()
    {
        typeof(OpenShopCommand),
        typeof(BuyCardCommand), typeof(BuyRelicCommand),
        typeof(BuyCardRemovalCommand), typeof(BuyPotionCommand),
    };

    /// <summary>
    /// Returns the concrete commands that can be dispatched right now,
    /// expanding each available command type against the current game state.
    /// </summary>
    public static List<ReplayCommand> GetAvailableCommands()
    {
        var types = GetDispatchableTypes();
        var commands = new List<ReplayCommand>();
        var state = RunStateProp?.GetValue(RunManager.Instance) as IRunState;
        var player = state?.Players.FirstOrDefault();

        foreach (var type in types)
        {
            // -- parameterless commands --
            if (type == typeof(EndTurnCommand))
                commands.Add(new EndTurnCommand());
            else if (type == typeof(OpenChestCommand))
                commands.Add(new OpenChestCommand());
            else if (type == typeof(TakeChestRelicCommand))
                commands.Add(new TakeChestRelicCommand());
            else if (type == typeof(ProceedToNextActCommand))
                commands.Add(new ProceedToNextActCommand());
            else if (type == typeof(OpenShopCommand))
                commands.Add(new OpenShopCommand());
            else if (type == typeof(OpenFakeShopCommand))
                commands.Add(new OpenFakeShopCommand());
            else if (type == typeof(ProceedToMapCommand))
            {
                if (ProceedToMapCommand.IsAvailable())
                    commands.Add(new ProceedToMapCommand());
            }
            else if (type == typeof(SkipRewardsCommand))
            {
                if (SkipRewardsCommand.IsAvailable())
                    commands.Add(new SkipRewardsCommand());
            }
            else if (type == typeof(CloseShopCommand))
            {
                var room = ReplayState.ActiveMerchantRoom;
                if (room != null && room.Inventory != null && room.Inventory.IsOpen)
                    commands.Add(new CloseShopCommand());
            }

            // -- combat card plays --
            else if (type == typeof(PlayCardCommand))
            {
                var combat = player?.PlayerCombatState;
                if (combat != null)
                {
                    var combatState = CombatManager.Instance?.DebugOnlyGetState();
                    var aliveEnemies = combatState?.Enemies
                        .Where(e => e.IsAlive).ToList();

                    foreach (var card in combat.Hand.Cards)
                    {
                        if (!MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb
                                .Instance.TryGetCardId(card, out uint id))
                            continue;

                        if (card.CanPlayTargeting(null))
                            commands.Add(new PlayCardCommand(id));

                        if (aliveEnemies != null)
                            foreach (var enemy in aliveEnemies)
                                if (card.CanPlayTargeting(enemy))
                                    commands.Add(new PlayCardCommand(id, enemy.CombatId));
                    }
                }
            }

            // -- potions --
            else if (type == typeof(UsePotionCommand))
            {
                if (player != null)
                {
                    bool inCombat = CombatManager.Instance?.IsInProgress == true;
                    var combatState = inCombat
                        ? CombatManager.Instance?.DebugOnlyGetState()
                        : null;

                    for (int i = 0; i < player.PotionSlots.Count; i++)
                    {
                        var potion = player.GetPotionAtSlotIndex(i);
                        if (potion == null) continue;

                        if (!inCombat && potion.Usage == MegaCrit.Sts2.Core.Entities.Potions.PotionUsage.CombatOnly)
                            continue;

                        bool needsEnemyTarget = potion.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.AnyEnemy;

                        if (!needsEnemyTarget)
                            commands.Add(new UsePotionCommand((uint)i));

                        if (needsEnemyTarget && combatState != null)
                            foreach (var enemy in combatState.Enemies)
                                if (enemy.IsAlive)
                                    commands.Add(new UsePotionCommand((uint)i, enemy.CombatId));
                    }
                }
            }
            else if (type == typeof(DiscardPotionCommand))
            {
                if (player != null)
                    for (int i = 0; i < player.PotionSlots.Count; i++)
                        if (player.GetPotionAtSlotIndex(i) != null)
                            commands.Add(new DiscardPotionCommand(i));
            }

            // -- map navigation --
            else if (type == typeof(MapMoveCommand))
            {
                // IsInstanceValid must be checked before ANY member touch — NMapScreen.Instance
                // keeps pointing at the freed map screen after it closes (e.g. once a room is
                // entered), and MapPointDictionaryField's reflective GetValue on a disposed
                // Godot.Control throws ObjectDisposedException (23x observed in the 89U passive
                // dump — see delta 8 in VENDORED-FROM.md). IsInsideTree() alone does not catch
                // this: a freed node still answers IsInsideTree() truthfully-but-uselessly right
                // up until the deref that throws.
                if (NMapScreen.Instance != null
                    && GodotObject.IsInstanceValid(NMapScreen.Instance)
                    && MapMoveCommand.MapPointDictionaryField?.GetValue(NMapScreen.Instance)
                        is Dictionary<MapCoord, NMapPoint> dict)
                {
                    var currentCoord = state?.CurrentMapCoord;
                    if (currentCoord.HasValue && dict.TryGetValue(currentCoord.Value, out var currentPoint) && state != null)
                    {
                        // MapTravel.GetTravelablePointsFrom (Core/Map/MapTravel.cs) is the game's
                        // authoritative travel rule: it returns the point's own Children, UNLESS a
                        // free-travel effect (e.g. Winged Boots) is active, in which case it returns
                        // the WHOLE next row instead. Reading `.Children` directly (as this used to)
                        // silently drops those free-travel destinations, illegally denying legal moves.
                        //
                        // PRE-BOSS ROW FIX: mirrors NMapScreen.RecalculateTravelability
                        // (NMapScreen.cs:401-405), which bypasses Children/MapTravel entirely at the
                        // row right before the boss and marks the boss point itself travelable — not
                        // every ActMap generator wires the boss point into every last-row point's
                        // Children (e.g. GoldenPathActMap.cs:66 only adds it to ONE last-row point),
                        // so without this the enumerated legal-move set silently omits the only real
                        // move from that row, denying it to the live bot's own decision-making even
                        // though a RECORDED replay move still dispatches fine (MapMoveCommand.Execute
                        // looks the destination up by raw coord, not through this enumeration).
                        var map = state.Map;
                        if (map != null && map.GetRowCount() > 0 && currentPoint.Point.coord.row == map.GetRowCount() - 1)
                        {
                            commands.Add(new MapMoveCommand(map.BossMapPoint.coord.col));
                        }
                        else
                        {
                            foreach (var child in MapTravel.GetTravelablePointsFrom(state, currentPoint.Point))
                                commands.Add(new MapMoveCommand(child.coord.col));
                        }
                    }
                    else
                    {
                        // First move of act — all row 0 nodes are reachable
                        foreach (var coord in dict.Keys)
                            if (coord.row == 0)
                                commands.Add(new MapMoveCommand(coord.col));
                    }
                }
            }

            // -- rest site --
            else if (type == typeof(ChooseRestSiteOptionCommand))
            {
                var sync = ReplayState.ActiveRestSiteSynchronizer;
                if (sync != null)
                    foreach (var option in sync.GetLocalOptions())
                        commands.Add(new ChooseRestSiteOptionCommand(option.OptionId));
            }

            // -- events --
            else if (type == typeof(ChooseEventOptionCommand))
            {
                var sync = ReplayState.ActiveEventSynchronizer;
                if (sync != null && sync.Events.Count > 0)
                {
                    if (sync.Events[0].IsFinished)
                        commands.Add(new ChooseEventOptionCommand(-1));
                    else
                        for (int i = 0; i < sync.Events[0].CurrentOptions.Count; i++)
                            commands.Add(new ChooseEventOptionCommand(i));
                }
            }

            // -- rewards --
            else if (type == typeof(ClaimRewardCommand))
            {
                // IsInstanceValid first — same trap as RunObsWriter.ClassifyReward (see that
                // file's comment): ActiveRewardsScreen keeps pointing at the freed rewards
                // screen after it closes, and IsInsideTree() alone doesn't catch a freed node.
                // 69x ObjectDisposedException observed here in the 89U passive dump (delta 8,
                // VENDORED-FROM.md) — this was the single largest source of lost decision lines.
                var screen = ReplayState.ActiveRewardsScreen;
                if (screen != null && GodotObject.IsInstanceValid(screen) && screen.IsInsideTree())
                {
                    int i = 0;
                    foreach (var _ in ClaimRewardCommand.EnumerateRewardButtons(screen))
                        commands.Add(new ClaimRewardCommand(i++));
                }
            }
            else if (type == typeof(TakeCardCommand))
            {
                // Same freed-screen trap as ClaimRewardCommand above — CardRewardSelectionScreen
                // can also outlive its node once the rewards screen closes, and the reflective
                // GetField/GetChildren below throws ObjectDisposedException on a disposed node.
                var screen = ReplayState.CardRewardSelectionScreen;
                if (screen != null && GodotObject.IsInstanceValid(screen))
                {
                    var cardRow = typeof(NCardRewardSelectionScreen)
                        .GetField("_cardRow", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(screen) as Godot.Node;
                    if (cardRow != null)
                    {
                        int count = 0;
                        foreach (Godot.Node child in cardRow.GetChildren())
                        {
                            var prop = child.GetType().GetProperty(
                                "CardModel", BindingFlags.Public | BindingFlags.Instance);
                            if (prop?.GetValue(child) is MegaCrit.Sts2.Core.Models.CardModel)
                                count++;
                        }
                        for (int i = 0; i < count; i++)
                            commands.Add(new TakeCardCommand(i));
                    }
                    commands.Add(TakeCardCommand.Skip());

                    var extras = typeof(NCardRewardSelectionScreen)
                        .GetField("_extraOptions", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(screen) as IReadOnlyList<MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative>;
                    if (extras != null)
                        foreach (var alt in extras)
                            if (alt.OptionId.Contains("sacrifice", StringComparison.OrdinalIgnoreCase)
                                || alt.OptionId.Contains("pael", StringComparison.OrdinalIgnoreCase))
                            {
                                commands.Add(TakeCardCommand.Sacrifice());
                                break;
                            }
                }
            }

            // -- selection screens --
            else if (type == typeof(SelectGridCardCommand))
            {
                // IsInstanceValid guard — GetDispatchableTypes() already checks this when it
                // adds the type to the set, but that check happens before this loop runs and a
                // screen can be freed in between (same class of bug as the reward/map screens).
                var screen = CardGridScreenCapture.ActiveScreen;
                if (screen != null && GodotObject.IsInstanceValid(screen))
                {
                    var cards = CardGridScreenCapture.GetSelectableCards(screen);
                    if (cards != null)
                        for (int i = 0; i < cards.Count; i++)
                            commands.Add(new SelectGridCardCommand(new[] { i }));
                }
            }
            else if (type == typeof(ClickGridCardCommand))
            {
                var screen = CardGridScreenCapture.ActiveScreen;
                if (screen != null && GodotObject.IsInstanceValid(screen))
                {
                    var cards = CardGridScreenCapture.GetSelectableCards(screen);
                    if (cards != null)
                        for (int i = 0; i < cards.Count; i++)
                            commands.Add(new ClickGridCardCommand(i));
                }
            }
            else if (type == typeof(ConfirmGridSelectionCommand))
            {
                var screen = CardGridScreenCapture.ActiveScreen;
                if (screen != null && GodotObject.IsInstanceValid(screen)
                    && ConfirmGridSelectionCommand.FindEnabledConfirmButton(screen) != null)
                    commands.Add(new ConfirmGridSelectionCommand());
            }
            else if (type == typeof(CancelGridSelectionCommand))
            {
                var screen = CardGridScreenCapture.ActiveScreen;
                if (screen != null && GodotObject.IsInstanceValid(screen)
                    && CancelGridSelectionCommand.FindEnabledBackButton(screen) != null)
                    commands.Add(new CancelGridSelectionCommand());
            }
            else if (type == typeof(SelectCardFromScreenCommand))
            {
                var screen = ChooseACardScreenCapture.ActiveScreen;
                if (screen != null && GodotObject.IsInstanceValid(screen))
                {
                    int count = 0;
                    foreach (Godot.Node node in screen.FindChildren("*", "", owned: false))
                    {
                        var prop = node.GetType().GetProperty(
                            "CardModel", BindingFlags.Public | BindingFlags.Instance);
                        if (prop?.GetValue(node) is MegaCrit.Sts2.Core.Models.CardModel)
                            count++;
                    }
                    for (int i = 0; i < count; i++)
                        commands.Add(new SelectCardFromScreenCommand(i));
                    commands.Add(new SelectCardFromScreenCommand(-1));
                }
            }
            else if (type == typeof(SelectHandCardsCommand))
            {
                var combatState = CombatManager.Instance?.DebugOnlyGetState();
                if (combatState != null)
                {
                    var p = combatState.Players.FirstOrDefault();
                    if (p != null)
                        for (int i = 0; i < p.PlayerCombatState.Hand.Cards.Count; i++)
                            commands.Add(new SelectHandCardsCommand(new[] { i }));
                }
            }
            else if (type == typeof(CrystalSphereClickCommand))
            {
                // Crystal sphere cells require deep grid reflection;
                // not enumerable without knowing dimensions.
                // (CrystalSphereReplayPatch.ActiveScreen is already IsInstanceValid-gated in
                // GetDispatchableTypes before this type is even added to the set.)
            }

            // -- shop purchases --
            else if (type == typeof(BuyCardCommand))
            {
                var room = ReplayState.ActiveMerchantRoom;
                if (room != null)
                {
                    var entries = OpenShopCommand.GetEntries(room);
                    if (entries != null)
                        foreach (var e in entries.OfType<MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry>())
                            if (e.EnoughGold && e.CreationResult?.Card?.Title != null)
                                commands.Add(new BuyCardCommand(e.CreationResult.Card.Title));
                }
            }
            else if (type == typeof(BuyRelicCommand))
            {
                List<MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry>? entries = null;
                var room = ReplayState.ActiveMerchantRoom;
                if (room != null)
                    entries = OpenShopCommand.GetEntries(room);
                if ((entries == null || entries.Count == 0) && ReplayState.FakeMerchantInstance != null)
                    entries = BuyRelicCommand.GetFakeMerchantEntries(ReplayState.FakeMerchantInstance);
                if (entries != null)
                    foreach (var e in entries.OfType<MegaCrit.Sts2.Core.Entities.Merchant.MerchantRelicEntry>())
                    {
                        var title = e.Model?.Title.GetFormattedText();
                        if (e.EnoughGold && title != null)
                            commands.Add(new BuyRelicCommand(title));
                    }
            }
            else if (type == typeof(BuyPotionCommand))
            {
                var room = ReplayState.ActiveMerchantRoom;
                if (room != null)
                {
                    var entries = OpenShopCommand.GetEntries(room);
                    if (entries != null)
                        foreach (var e in entries.OfType<MegaCrit.Sts2.Core.Entities.Merchant.MerchantPotionEntry>())
                        {
                            var title = e.Model?.Title.GetFormattedText();
                            if (e.EnoughGold && title != null)
                                commands.Add(new BuyPotionCommand(title));
                        }
                }
            }
            else if (type == typeof(BuyCardRemovalCommand))
            {
                var room = ReplayState.ActiveMerchantRoom;
                if (room != null)
                {
                    var entries = OpenShopCommand.GetEntries(room);
                    if (entries != null)
                    {
                        var removal = entries.OfType<MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardRemovalEntry>().FirstOrDefault();
                        if (removal != null && removal.EnoughGold)
                            commands.Add(new BuyCardRemovalCommand());
                    }
                }
            }
        }

        return commands;
    }

    public static HashSet<Type> GetDispatchableTypes()
    {
        bool blocked = ReplayState.PotionInFlight
                    || ReplayState.CardPlayInFlight
                    || CardPlayReplayPatch.IsAwaitingEndTurnCompletion
                    || MapMoveInFlight
                    || ReplayState.ActionInFlight;

        
        var types = new HashSet<Type>();
        
        if (CardGridScreenCapture.ActiveScreen != null
            && GodotObject.IsInstanceValid(CardGridScreenCapture.ActiveScreen)
            && CardGridScreenCapture.ActiveScreen.IsInsideTree())
        {
            types.Add(typeof(SelectGridCardCommand));
            types.Add(typeof(ClickGridCardCommand));
            types.Add(typeof(ConfirmGridSelectionCommand));
            types.Add(typeof(CancelGridSelectionCommand));
        }
        if (ChooseACardScreenCapture.ActiveScreen != null
            && GodotObject.IsInstanceValid(ChooseACardScreenCapture.ActiveScreen)
            && ChooseACardScreenCapture.ActiveScreen.IsInsideTree())
            types.Add(typeof(SelectCardFromScreenCommand));
        if (HandSelectionCapture.ActiveHand != null
            && GodotObject.IsInstanceValid(HandSelectionCapture.ActiveHand)
            && HandSelectionCapture.ActiveHand.IsInsideTree())
            types.Add(typeof(SelectHandCardsCommand));
        
        if (blocked)
        {
            return types;
        }

        // TODO: further refine potion commands to only include potions usable in
        // the current context (e.g. combat-only potions gated behind IsInProgress)
        if (LocalPlayerHasPotions())
        {
            types.Add(typeof(UsePotionCommand));
            types.Add(typeof(DiscardPotionCommand));
        }

        if (CardGridScreenCapture.ActiveScreen != null
            && GodotObject.IsInstanceValid(CardGridScreenCapture.ActiveScreen)
            && CardGridScreenCapture.ActiveScreen.IsInsideTree())
        {
            types.Add(typeof(SelectGridCardCommand));
            types.Add(typeof(ClickGridCardCommand));
            types.Add(typeof(ConfirmGridSelectionCommand));
            types.Add(typeof(CancelGridSelectionCommand));
        }
        if (ChooseACardScreenCapture.ActiveScreen != null
            && GodotObject.IsInstanceValid(ChooseACardScreenCapture.ActiveScreen)
            && ChooseACardScreenCapture.ActiveScreen.IsInsideTree())
            types.Add(typeof(SelectCardFromScreenCommand));
        if (HandSelectionCapture.ActiveHand != null
            && GodotObject.IsInstanceValid(HandSelectionCapture.ActiveHand)
            && HandSelectionCapture.ActiveHand.IsInsideTree())
            types.Add(typeof(SelectHandCardsCommand));

        if (CrystalSphereReplayPatch.ActiveScreen != null
            && GodotObject.IsInstanceValid(CrystalSphereReplayPatch.ActiveScreen))
            types.Add(typeof(CrystalSphereClickCommand));

        if (CombatManager.Instance.IsInProgress && CardPlayReplayPatch.ResolveLocalPlayer()?.PlayerCombatState?.Phase == PlayerTurnPhase.Play)
        {
            types.Add(typeof(PlayCardCommand));
            types.Add(typeof(EndTurnCommand));
        }

        if (NMapScreen.Instance != null
            && GodotObject.IsInstanceValid(NMapScreen.Instance)
            && NMapScreen.Instance.IsTravelEnabled)
            types.Add(typeof(MapMoveCommand));

        var currentRoom = GetCurrentRoom();

        if (currentRoom is RestSiteRoom)
        {
            types.Add(typeof(ChooseRestSiteOptionCommand));
            types.Add(typeof(ProceedToMapCommand));
        }

        if (currentRoom is TreasureRoom)
        {
            types.Add(typeof(OpenChestCommand));
            types.Add(typeof(TakeChestRelicCommand));
            types.Add(typeof(ProceedToMapCommand));
        }

        if (ReplayState.ActiveRewardsScreen != null
            && GodotObject.IsInstanceValid(ReplayState.ActiveRewardsScreen)
            && ReplayState.ActiveRewardsScreen.IsInsideTree())
        {
            types.Add(typeof(ClaimRewardCommand));
            types.Add(typeof(TakeCardCommand));
            types.Add(typeof(ProceedToMapCommand));
            types.Add(typeof(SkipRewardsCommand));

            if (currentRoom != null
                && (currentRoom.RoomType == RoomType.Boss || currentRoom.IsVictoryRoom))
                types.Add(typeof(ProceedToNextActCommand));
        }

        // IsInstanceValid + IsInsideTree guard — same freed-screen trap as NMapScreen.Instance /
        // ActiveRewardsScreen above (delta 9, VENDORED-FROM.md): without SubscribeToRoomEvents()
        // ever firing OnRoomExited, this reference stuck non-null forever after the shop closed,
        // mislabelling every subsequent Map/Rest decision as Shop.
        if (ReplayState.ActiveMerchantRoom != null
            && GodotObject.IsInstanceValid(ReplayState.ActiveMerchantRoom)
            && ReplayState.ActiveMerchantRoom.IsInsideTree())
        {
            types.UnionWith(ShopCommandTypes);
            types.Add(typeof(CloseShopCommand));
            types.Add(typeof(ProceedToMapCommand));
        }

        if (ReplayState.FakeMerchantInstance != null)
        {
            types.Add(typeof(OpenFakeShopCommand));
            types.Add(typeof(BuyRelicCommand));
        }

        if (currentRoom is EventRoom)
        {
            // Events can legitimately coexist with other dispatchable commands:
            // Future of Potions embeds a fake merchant (OpenFakeShop/BuyRelic),
            // events offer potions (UsePotion/DiscardPotion), and the map
            // screen's MapMoveCommand lingers from the room entry.  None of
            // those should suppress the event's own option selection.
            types.Add(typeof(ChooseEventOptionCommand));
        }

        // If the next queued command is a PROCEED sentinel, allow dispatching
        // it even when we're no longer in an EventRoom.  Some events (e.g.
        // Battleworn Dummy) auto-proceed when their sub-combat resolves, so
        // the recorded `-1` has no UI to act on — the command just needs to
        // be consumed so the replay can advance.
        if (ReplayEngine.PeekNext(out ReplayCommand? nextCmd)
            && nextCmd is ChooseEventOptionCommand evtCmd
            && evtCmd.RecordedIndex == -1)
        {
            types.Add(typeof(ChooseEventOptionCommand));
        }

        return types;
    }

    private static bool _dispatchPollRunning;
    private static HashSet<Type>? _lastDispatchableTypes;

    public static void StartDispatchPoll()
    {
        if (_dispatchPollRunning) return;
        _dispatchPollRunning = true;
        EnsureEmitter();
        PlayerActionBuffer.LogMigrationWarning("connected");
        _emitter!.Connect(DispatchSignalEmitter.SignalInputRequired,
            Callable.From(() =>
            {
                PlayerActionBuffer.LogMigrationWarning("signal fire");
                var state = new GameStateSnapshot(GetDispatchableTypes());
                var json = System.Text.Json.JsonSerializer.Serialize(state,
                    new System.Text.Json.JsonSerializerOptions
                    { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

                var cmds = GetAvailableCommands();
                foreach (var cmd in cmds)
                    PlayerActionBuffer.LogMigrationWarning(cmd.ToString());
            }));
        SubscribeToRoomEvents();
        ScheduleDispatchPollTick();
    }

    private static void ScheduleDispatchPollTick()
    {
        if (!_dispatchPollRunning) return;
        NGame.Instance?.GetTree()?.CreateTimer(0.5).Connect(
            "timeout", Callable.From(DispatchPollTick));
    }

    public static void ClearDispatchableCache()
    {
        _lastDispatchableTypes?.Clear();
    }

    private static void LogDispatchableChanges()
    {
        var current = GetDispatchableTypes();
        if (_lastDispatchableTypes == null || !current.SetEquals(_lastDispatchableTypes))
        {
            _lastDispatchableTypes = new HashSet<Type>(current);

            if (current.Count > 0 && _emitter != null && GodotObject.IsInstanceValid(_emitter))
                _emitter.EmitSignal("InputRequired");
        }
    }

    private static void DispatchPollTick()
    {
        LogDispatchableChanges();
        ScheduleDispatchPollTick();
    }

    public static void Load(IReadOnlyList<string> commands)
    {
        ReplayEngine.Load(commands);
    }

    private static bool _paused;
    private static bool _stepping;
    public static bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value) TryDispatch();
        }
    }

    /// <summary>
    /// Executes a single command while paused, then re-pauses.
    /// </summary>
    public static void Step()
    {
        if (!_paused) Paused = true;
        _stepping = true;
        DispatchNow();
    }
    private static float _delayBetweenCommands = 1.0f;
    /// <summary>
    /// True while a dispatched command is executing (between ExecuteNext and
    /// the command being consumed from the queue).  Prevents re-dispatch of
    /// the same command.
    /// </summary>
    private static bool _dispatchInProgress;

    /// <summary>
    /// The command string that was last dispatched.  Used to detect when the
    /// queue front has changed (i.e. the previous command was consumed).
    /// </summary>
    private static ReplayCommand? _lastDispatchedCmd;

    /// <summary>
    /// Incremented each time a dispatch is scheduled.  Timer callbacks compare
    /// against this to detect if they've been superseded by DispatchNow.
    /// </summary>
    private static int _dispatchGeneration;

    /// <summary>Timestamp of the last successful command dispatch (System.Environment.TickCount64).</summary>
    private static long _lastDispatchTick;

    /// <summary>Whether the stall watchdog timer is running.</summary>
    private static bool _watchdogRunning;

    /// <summary>
    /// Game speed multiplier during replay. 1.0 = normal, 2.0 = double speed, etc.
    /// Applied via Engine.TimeScale when replay is active.
    /// Set to 1.0 to restore normal speed.
    /// </summary>
    private static float _gameSpeed = 2.0f;
    public static float GameSpeed
    {
        get => _gameSpeed;
        set
        {
            _gameSpeed = Math.Max(0.1f, value);
            if (ReplayEngine.IsActive)
                Engine.TimeScale = _gameSpeed;
        }
    }

    /// <summary>
    /// Applies the game speed when replay starts.
    /// Called from ReplayEngine.Load or when replay becomes active.
    /// </summary>
    public static void ApplyGameSpeed()
    {
        if (ReplayEngine.IsActive)
            Engine.TimeScale = _gameSpeed;
    }

    /// <summary>
    /// Restores normal game speed. Called when replay ends or is cancelled.
    /// </summary>
    public static void RestoreGameSpeed()
    {
        Engine.TimeScale = 1.0;
    }

    /// <summary>
    /// Tracks whether a map move is in progress.  Set when a MoveToMapCoordAction
    /// is dispatched, cleared when a room readiness signal arrives (Combat, Event,
    /// Shop, RestSite, Treasure).  Blocks dispatch while the new room loads.
    /// </summary>
    public static bool MapMoveInFlight { get; set; }

    /// <summary>
    /// Called when combat effects have settled after a card play, potion use,
    /// etc.  Bypasses the delay timer for immediate dispatch.
    /// </summary>
    public static void NotifyEffectsSettled()
    {
        DispatchNow();
    }

    /// <summary>
    /// Bypasses the delay timer and dispatches the next command immediately.
    /// Called by patches when they know the game is ready and waiting would
    /// cause a visible stall (e.g. after effects settle, after a screen opens).
    /// </summary>
    public static void DispatchNow()
    {
        ++_dispatchGeneration; // Invalidate any pending timer callback.
        _dispatchInProgress = false;
        _lastDispatchedCmd = null;
        Callable.From(ExecuteNext).CallDeferred();
    }

    /// <summary>
    /// Called by ReplayEngine.SignalConsumed when a command is dequeued.
    /// Clears the in-progress guard and re-triggers dispatch for the next command.
    /// </summary>
    internal static void NotifyConsumed()
    {
        _dispatchInProgress = false;
        _lastDispatchedCmd = null;

        // Don't re-trigger immediately — respect the delay between commands.
        int gen = ++_dispatchGeneration;
        if (_delayBetweenCommands > 0)
        {
            NGame.Instance?.GetTree()?.CreateTimer(_delayBetweenCommands).Connect(
                "timeout", Callable.From(() =>
                {
                    if (_dispatchGeneration == gen)
                        TryDispatch();
                }));
        }
        else
        {
            Callable.From(() =>
            {
                if (_dispatchGeneration == gen)
                    TryDispatch();
            }).CallDeferred();
        }
    }

    /// <summary>
    /// Stops the replay mid-flight and transitions to recording mode.
    /// Restores already-consumed commands to the action buffer so the
    /// next save log contains the full history.
    /// </summary>
    public static void StopAndRecord()
    {
        // Calculate consumed commands: _loadedCommands minus what's still in _pending.
        var loaded = ReplayEngine._loadedCommands;
        int remaining = ReplayEngine._pending.Count;
        int consumed = loaded.Count - remaining;

        if (consumed > 0)
        {
            var consumedCommands = loaded.GetRange(0, consumed);
            // Fire ReplayCompleted with only the consumed portion so the buffer
            // gets the commands that were actually replayed.
            ReplayEngine.FireReplayCompleted(consumedCommands);
        }

        Clear();
    }

    /// <summary>
    /// Compares the battle state recorded before this command with the live
    /// state at first execution and logs a divergence to the diagnostic log.
    /// Diagnostic only — never blocks dispatch. Checked once per command so
    /// retries don't spam (the state legitimately mutates while a command
    /// retries, e.g. waiting on an enemy spawn).
    /// </summary>
    private static void CheckPreState(ReplayCommand cmd)
    {
        if (cmd.PreStateChecked || cmd.ExpectedPreState == null)
            return;
        cmd.PreStateChecked = true;

        try
        {
            string? actual = PlayerActionBuffer.GetBattleStateSummary();
            if (actual == null || actual == cmd.ExpectedPreState)
                return;

            DiagnosticLog.Write("StateCheck",
                $"divergence before {cmd}:");
            DiagnosticLog.Write("StateCheck", $"  expected: {cmd.ExpectedPreState}");
            DiagnosticLog.Write("StateCheck", $"  actual:   {actual}");
            DiagnosticLog.Write("StateCheck", $"  detail:   {DescribeFullCombatState()}");
        }
        catch
        {
            // State summaries are best-effort diagnostics only.
        }
    }

    /// <summary>
    /// Full live combat dump for divergence triage: energy, then every creature
    /// on both sides (allies include the player and minions) with HP, block,
    /// and power stacks. Recorded pre-states only cover hand + enemy HP, so
    /// this is what exposes a missing minion or power at the divergence point.
    /// </summary>
    private static string DescribeFullCombatState()
    {
        try
        {
            var state = CombatManager.Instance?.DebugOnlyGetState();
            if (state == null)
                return "(no combat state)";

            var sb = new System.Text.StringBuilder();

            MegaCrit.Sts2.Core.Entities.Players.Player? me;
            try { me = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(state); }
            catch { me = state.Players.FirstOrDefault(); }
            if (me?.PlayerCombatState != null)
                sb.Append($"energy={me.PlayerCombatState.Energy} ");

            sb.Append("creatures=[");
            bool first = true;
            foreach (var c in state.Creatures)
            {
                if (!first) sb.Append("; ");
                first = false;
                sb.Append($"{c.CombatId?.ToString() ?? "?"}:{c.Name} {c.CurrentHp}/{c.MaxHp}");
                if (c.Block > 0)
                    sb.Append($" blk{c.Block}");
                if (c.Powers is { Count: > 0 })
                    sb.Append(" (")
                      .Append(string.Join(",", c.Powers.Select(p => $"{p.Id.Entry}:{p.Amount}")))
                      .Append(')');
            }
            sb.Append(']');
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"(state dump failed: {ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>Clears the replay queue and resets all patch state.</summary>
    public static void Clear()
    {
        ReplayEngine._pending.Clear();
        ReplayEngine._recentConsumed.Clear();
        ReplayEngine._replayActive = false;
        ReplayEngine.IsReplayRun = false;
        ReplayEngine.ActiveActs = null;
        RestoreGameSpeed();
        ResetAllPatchState();
    }

    /// <summary>
    /// Resets all static state across recording and replay patches so that
    /// both paths can start cleanly after a stall or cancellation.
    /// </summary>
    public static void ResetAllPatchState()
    {
        // Recording state
        BattleRewardPatch.IsProcessingCardReward = false;
        DeckRemovalState.PendingRemoval = false;
        ShopPurchaseState.IsPurchasing = false;
        ShopPurchaseState.PendingLabel = null;
        EventSelectionPatch.PendingIndex = null;
        SimpleGridContext.Pending = false;
        HandCardSelectRecordPatch.SuppressNext = false;

        // Crystal sphere
        CrystalSphereReplayPatch.PendingTool = null;

        // Dispatcher
        Reset();
    }

    /// <summary>Resets all dispatcher state.  Called on replay start and clear.</summary>
    public static void Reset()
    {
        ReplayState.Reset();
        _paused = false;
        _dispatchInProgress = false;
        _lastDispatchedCmd = null;
        MapMoveInFlight = false;
        _lastDispatchTick = System.Environment.TickCount64;
        ++_dispatchGeneration;
        _lastDispatchableTypes = null;
        RestoreGameSpeed();
        StartWatchdog();
        SubscribeToRoomEvents();
    }

    private static bool _subscribedToRoomEvents;

    private static void SubscribeToRoomEvents()
    {
        if (_subscribedToRoomEvents) return;
        _subscribedToRoomEvents = true;
        RunManager.Instance.RoomEntered += OnRoomEntered;
        RunManager.Instance.RoomExited += OnRoomExited;
    }

    private static void OnRoomEntered()
    {
        var room = GetCurrentRoom();
        GD.Print($"[RunReplays] OnRoomEntered — room={room?.GetType().Name ?? "null"} active={ReplayEngine.IsActive}");
        if (!ReplayEngine.IsActive) return;

        MapMoveInFlight = false;
        TryDispatch();
    }

    private static void OnRoomExited()
    {
        var room = GetCurrentRoom();
        GD.Print($"[RunReplays] OnRoomExited — room={room?.GetType().Name ?? "null"} active={ReplayEngine.IsActive}");
        ReplayState.ActiveMerchantRoom = null;
        // Fix 1 (round-4 obs-parity, delta 10 in VENDORED-FROM.md) — same stale-reference
        // class of bug as ActiveMerchantRoom above: CardGridScreenCapture.ActiveScreen was only
        // ever cleared by SelectGridCardCommand.Execute(), which never runs in passive-dump
        // mode (PassiveDump only observes CheckPreState, it never dispatches synthetic
        // commands), so a resolved grid screen stayed IsInstanceValid+IsInsideTree and hijacked
        // DecisionContext.Classify's SelectCards-before-Map/Event/Shop/Rest priority order for
        // every decision until an unrelated room transition happened to free the node. Clearing
        // it here — regardless of which command resolved the screen — closes that gap the same
        // way delta 9 closed it for the merchant room.
        SpireBot.Replay.Commands.CardGridScreenCapture.Clear();

        if (!ReplayEngine.IsActive) return;

        ReplayState.DrainScreenCleanup();
        ReplayState.ActiveEventSynchronizer = null;
        TreasureRoomReplayPatch.ActiveRoom = null;

        // Auto-proceeding events (e.g. Battleworn Dummy after its sub-combat)
        // fire RoomExited but don't reliably fire a matching RoomEntered.
        // Pump dispatch here so a queued PROCEED sentinel can consume itself.
        TryDispatch();
    }

    /// <summary>
    /// Starts a recurring 2-second watchdog that checks for stalled map moves.
    /// If no command has been dispatched for 5+ seconds and the next command
    /// is a map move, forces readiness and dispatches it.
    /// </summary>
    public static void StartWatchdog()
    {
        if (_watchdogRunning) return;
        _watchdogRunning = true;
        ScheduleWatchdogTick();
    }

    private static void ScheduleWatchdogTick()
    {
        if (!_watchdogRunning) return;
        NGame.Instance?.GetTree()?.CreateTimer(1.0).Connect(
            "timeout", Callable.From(WatchdogTick));
    }

    private static void WatchdogTick()
    {
        if (!ReplayEngine.IsActive)
        {
            _watchdogRunning = false;
            return;
        }
        
        long elapsed = System.Environment.TickCount64 - _lastDispatchTick;

        if (elapsed >= 5000
            && ReplayEngine.PeekNext(out ReplayCommand? cmd) && cmd != null
            && cmd is MapMoveCommand)
        {
            // Force-clear blockers so dispatch can proceed.
            ReplayState.ClearActionInFlight();
            MapMoveInFlight = false;
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            NMapScreen.Instance?.Open();
            NMapScreen.Instance?.SetTravelEnabled(true);
            DispatchNow();
        }

        ScheduleWatchdogTick();
    }

    /// <summary>
    /// Checks if the next queued command can execute given the current readiness
    /// state, and if so, executes it.
    /// </summary>
    internal static void TryDispatch([System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        ReplayEngine.PeekNext(out ReplayCommand? peekCmd);
        GD.Print($"[RunReplays] TryDispatch caller={caller} active={ReplayEngine.IsActive} paused={_paused} next={peekCmd?.GetType().Name ?? "null"}({peekCmd})");
        DiagnosticLog.Write("Dispatch",
            $"TryDispatch caller={caller} active={ReplayEngine.IsActive} paused={_paused} " +
            $"next={peekCmd?.GetType().Name ?? "null"}({peekCmd})");

        LogDispatchableChanges();

        if (!ReplayEngine.IsActive || _paused)
            return;

        if (!ReplayEngine.PeekNext(out ReplayCommand? cmd) || cmd == null)
            return;

        var dispatchable = GetDispatchableTypes();
        if (!dispatchable.Contains(cmd.GetType()))
        {
            string dispatchableNames = string.Join(",", dispatchable.Select(t => t.Name));
            GD.Print($"[RunReplays] TryDispatch miss — cmd={cmd.GetType().Name} dispatchable=[{dispatchableNames}]");
            DiagnosticLog.Write("Dispatch",
                $"miss — cmd={cmd.GetType().Name}({cmd}) dispatchable=[{dispatchableNames}]");
            return;
        }

        // Don't re-dispatch the same command that's already in progress.
        if (_dispatchInProgress && cmd == _lastDispatchedCmd)
        {
            GD.Print($"[RunReplays] TryDispatch skip — already in progress ({cmd.GetType().Name})");
            return;
        }

        _dispatchInProgress = true;
        _lastDispatchedCmd = cmd;

        int gen = ++_dispatchGeneration;
        if (_delayBetweenCommands > 0)
        {
            NGame.Instance!.GetTree()!.CreateTimer(_delayBetweenCommands).Connect(
                "timeout", Callable.From(() =>
                {
                    if (_dispatchGeneration == gen)
                    {
                        ExecuteNext();
                    }
                }));
        }
        else
        {
            Callable.From(() =>
            {
                if (_dispatchGeneration == gen)
                    ExecuteNext();
            }).CallDeferred();
        }
    }

    private static void scheduleDispatchOnDelay()
    {
        int gen = ++_dispatchGeneration;
        NGame.Instance?.GetTree()?.CreateTimer(0.3f).Connect(
            "timeout", Callable.From(() =>
            {
                if (_dispatchGeneration == gen)
                    TryDispatch();
            }));
    }

    private static void ExecuteNext()
    {
        ReplayEngine.PeekNext(out ReplayCommand? peekCmd);
        GD.Print($"[RunReplays] ExecuteNext entry — active={ReplayEngine.IsActive} paused={_paused} next={peekCmd?.GetType().Name ?? "null"}({peekCmd})");

        LogDispatchableChanges();

        // Re-apply speed in case the game reset Engine.TimeScale during a transition.
        if (ReplayEngine.IsActive && Engine.TimeScale != _gameSpeed)
            Engine.TimeScale = _gameSpeed;

        if (!ReplayEngine.IsActive)
            return;

        if (_paused && !_stepping)
            return;

        _stepping = false;

        if (!ReplayEngine.PeekNext(out ReplayCommand? cmd) || cmd == null)
            return;

        if (!GetDispatchableTypes().Contains(cmd.GetType()))
        {
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            scheduleDispatchOnDelay();
            return;
        }

        _lastDispatchTick = System.Environment.TickCount64;

        CheckPreState(cmd);

        DiagnosticLog.Write("Dispatch", $"execute → {cmd.GetType().Name}({cmd})");
        ExecuteResult result;
        try
        {
            result = cmd.Execute();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Dispatch",
                $"EXCEPTION in {cmd.GetType().Name}({cmd}): {ex.GetType().Name}: {ex.Message}");
            DiagnosticLog.Write("Dispatch", ex.StackTrace ?? "<no stack>");
            throw;
        }

        if (result.Success)
        {
            DiagnosticLog.Write("Dispatch", $"ok → consumed {cmd.GetType().Name}");
            ReplayEngine.ConsumeAny();
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            int gen = ++_dispatchGeneration;
            NGame.Instance?.GetTree()?.CreateTimer(0.5f).Connect(
                "timeout", Callable.From(() =>
                {
                    if (_dispatchGeneration == gen)
                        TryDispatch();
                }));
            return;
        }
        if (result.RetryDelayMs > 0)
        {
            DiagnosticLog.Write("Dispatch",
                $"retry in {result.RetryDelayMs}ms → {cmd.GetType().Name}");
            _dispatchInProgress = false;
            _lastDispatchedCmd = null;
            int gen = ++_dispatchGeneration;
            NGame.Instance?.GetTree()?.CreateTimer(result.RetryDelayMs / 1000f).Connect(
                "timeout", Callable.From(() =>
                {
                    if (_dispatchGeneration == gen)
                        TryDispatch();
                }));
            return;
        }

        DiagnosticLog.Write("Dispatch", $"UNRECOGNISED result from {cmd.GetType().Name}({cmd})");
        PlayerActionBuffer.LogMigrationWarning($"[Dispatcher] Unrecognised command: {cmd}");
    }


    private static DispatchSignalEmitter? _emitter;
    public static DispatchSignalEmitter? Emitter => _emitter;

    private static void EnsureEmitter()
    {
        if (_emitter != null && GodotObject.IsInstanceValid(_emitter)) return;
        _emitter = new DispatchSignalEmitter();
        _emitter.Name = "DispatchSignalEmitter";
        NGame.Instance?.AddChild(_emitter);
    }
}

public partial class DispatchSignalEmitter : Node
{
    public const string SignalInputRequired = "InputRequired";

    public DispatchSignalEmitter()
    {
        AddUserSignal(SignalInputRequired);
    }
}
