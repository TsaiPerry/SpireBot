using System;

using BaseLib.Config;
using Godot;

using SpireBot.Replay;

namespace SpireBot.SpireBotCode.Overlay;

/// <summary>
/// The overlay's live transport controls: pause/play, a speed ladder (◀ 2.0x ▶), and a step button
/// that only appears while paused. Mirrors RunReplays' replay control bar
/// (RunReplays/RunOverlay.cs:189-343) for the live bot.
///
/// Owned by <see cref="ThinkingOverlay"/>, which adds one instance as the first row of its panel
/// and pumps <see cref="RefreshControls"/> from its own <c>_Process</c>. Everything this touches
/// (<see cref="BotController.Paused"/>, <see cref="SpireBotConfig.GameSpeed"/>,
/// <see cref="ReplayDispatcher.GameSpeed"/>) is static, so this node holds no state of its own
/// beyond its child widgets — which is why all the handlers below are static too.
/// </summary>
public sealed partial class BotControlBar : HBoxContainer
{
    private Button? _pauseButton;
    private Button? _stepButton;
    private Label? _speedLabel;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 4);
        // These buttons sit over the live game board; without this, clicks fall through to
        // whatever is underneath (a card, a map node).
        MouseFilter = MouseFilterEnum.Stop;

        _pauseButton = MakeButton("⏸ Pause", OnPausePressed);
        AddChild(_pauseButton);

        AddChild(MakeButton("◀", OnSpeedDown));

        _speedLabel = new Label
        {
            CustomMinimumSize = new Vector2(48, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _speedLabel.AddThemeFontSizeOverride("font_size", 14);
        _speedLabel.AddThemeColorOverride("font_color", Colors.White);
        AddChild(_speedLabel);

        AddChild(MakeButton("▶", OnSpeedUp));

        _stepButton = MakeButton("⏭ Step", OnStepPressed);
        AddChild(_stepButton);

        RefreshControls();
    }

    /// <summary>Plain Godot Buttons styled to match ThinkingOverlay's existing 14px white-on-black
    /// look. Deliberately NOT RunReplays' MakeArrowButton, which loads
    /// res://images/atlases/ui_atlas.sprites/settings_tiny_*_arrow.tres — text glyphs keep this
    /// overlay free of game-asset dependencies.</summary>
    private static Button MakeButton(string text, Action onPressed)
    {
        var button = new Button { Text = text };
        button.AddThemeFontSizeOverride("font_size", 14);
        button.Connect(BaseButton.SignalName.Pressed, Callable.From(onPressed));
        return button;
    }

    private static void OnPausePressed()
    {
        BotController.Paused = !BotController.Paused;
        ApplyEffectiveSpeed();
    }

    private static void OnStepPressed() => BotController.StepOnce();

    private static void OnSpeedDown() => SetSelectedSpeed(SpeedLadder.Prev(SpireBotConfig.GameSpeed));

    private static void OnSpeedUp() => SetSelectedSpeed(SpeedLadder.Next(SpireBotConfig.GameSpeed));

    /// <summary>Records the user's chosen multiplier: in memory, on disk (debounced), and — via
    /// <see cref="ApplyEffectiveSpeed"/> — in the running game. Writing SpireBotConfig is what
    /// makes a live change survive StartBotRun's re-pin (BotController.cs:189) into the next
    /// run.</summary>
    private static void SetSelectedSpeed(float speed)
    {
        SpireBotConfig.GameSpeed = speed;
        ModConfig.SaveDebounced<SpireBotConfig>();
        ApplyEffectiveSpeed();
    }

    /// <summary>
    /// Pushes the multiplier the game should actually be running at right now: the user's
    /// selection while the bot is deciding, 1.0 while paused so the board can be read at normal
    /// speed.
    ///
    /// This MUST go through ReplayDispatcher.GameSpeed rather than assigning Engine.TimeScale
    /// directly. The vendored ExecuteNext re-asserts TimeScale from its own _gameSpeed field on
    /// every dispatch (Replay/ReplayDispatcher.cs:1112-1114), above its own paused check.
    /// RunReplays can assign TimeScale directly on pause only because its pause also
    /// short-circuits TryDispatch (Replay/ReplayDispatcher.cs:1046) so ExecuteNext never runs;
    /// SpireBot's pause deliberately leaves that path running, so a direct assignment here would
    /// be stomped back to the fast value within a frame.
    /// </summary>
    private static void ApplyEffectiveSpeed()
    {
        ReplayDispatcher.GameSpeed = BotController.Paused ? 1.0f : SpireBotConfig.GameSpeed;
    }

    /// <summary>Re-renders the bar from current state. Cheap and idempotent — ThinkingOverlay
    /// calls it every frame rather than wiring change events for a five-widget bar.</summary>
    public void RefreshControls()
    {
        // Defensive only: today ThinkingOverlay constructs and AddChild's this bar inside its own
        // _Ready, and Godot runs a child's _Ready synchronously during AddChild, so these fields
        // are always set by the time RefreshControls can run. Guards against a future caller
        // invoking this before the node is ready.
        if (_pauseButton == null || _stepButton == null || _speedLabel == null) return;

        Visible = BotController.IsRunning;
        if (!Visible) return;

        bool paused = BotController.Paused;
        _pauseButton.Text = paused ? "▶ Play" : "⏸ Pause";
        _stepButton.Visible = paused;
        _speedLabel.Text = $"{SpireBotConfig.GameSpeed:0.0}x";
    }
}
