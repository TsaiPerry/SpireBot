using Godot;

using SpireBot.SpireBotCode.Policies;

namespace SpireBot.SpireBotCode.Overlay;

/// <summary>
/// Task 14: a persistent, top-left, semi-transparent panel that renders the bot's most recent
/// decision — kind, chosen action label, and the policy's top-K distribution as text + bars.
/// Subscribes to <see cref="BotController.DecisionMade"/> for its data.
///
/// Lifecycle: <see cref="Ensure"/> is idempotent (safe to call repeatedly from any thread) and
/// is called both from <see cref="BotController.PrepareDriver"/> (every attach) and lazily on
/// the very first <see cref="BotController.DecisionMade"/> event, so the overlay exists exactly
/// once no matter which path runs first. The node is added directly under the active
/// <c>SceneTree.Root</c> — not under any screen-specific node — so it survives every scene
/// change (map -> combat -> shop -> ...) for the life of the process, the same pattern
/// <c>((SceneTree)Engine.GetMainLoop()).Root</c> gives any persistent overlay in Godot 4 when a
/// mod has no HUD/autoload node of its own to parent under.
///
/// <see cref="BotController.DecisionMade"/> can fire from whatever context
/// <c>ActionExecutor</c>/dispatch signal handling runs on (see BotController's own doc
/// comment on that chain) — every mutation of this node's children therefore goes through
/// <c>CallDeferred</c> rather than touching the scene tree directly from the event handler.
///
/// Toggled via <see cref="SpireBotConfig.ShowOverlay"/> without ever unsubscribing from
/// DecisionMade — flipping the config flag back on shows the latest decision immediately
/// rather than a stale/blank panel.
/// </summary>
public sealed partial class ThinkingOverlay : CanvasLayer
{
    private static ThinkingOverlay? _instance;
    private static bool _subscribed;

    /// <summary>True while a ShowStatus message (a pre-attach failure like a bad contract
    /// path) should keep the panel visible even though no run is attached — without this,
    /// gating visibility on IsRunning would make exactly those failures invisible.</summary>
    private static bool _showingStatus;

    private Label _kindLabel = null!;
    private Label _actionLabel = null!;
    private VBoxContainer _topKBox = null!;
    private BotControlBar _controlBar = null!;

    /// <summary>
    /// Idempotently ensures the overlay node exists in the scene tree and is subscribed to
    /// <see cref="BotController.DecisionMade"/>. Safe to call from any thread/context or any
    /// number of times — the actual <c>AddChild</c> is deferred onto the main thread and only
    /// happens once (guarded by <see cref="_instance"/>).
    /// </summary>
    public static void Ensure()
    {
        if (!_subscribed)
        {
            BotController.DecisionMade += OnDecisionMade;
            _subscribed = true;
        }

        if (_instance != null && GodotObject.IsInstanceValid(_instance))
            return;

        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            GD.PrintErr("[SpireBot] ThinkingOverlay.Ensure: Engine.GetMainLoop() is not a " +
                        "SceneTree — cannot attach the overlay.");
            return;
        }

        var overlay = new ThinkingOverlay();
        _instance = overlay;
        tree.Root.CallDeferred(Node.MethodName.AddChild, overlay);
    }

    /// <summary>
    /// Shows a one-line status/error in the overlay. Used for failures that would otherwise be
    /// invisible in game (an attach that aborts before its first decision would otherwise leave
    /// no visible sign anything went wrong). Safe from any thread — the UI write is deferred like
    /// the decision updates.
    /// </summary>
    public static void ShowStatus(string message)
    {
        _showingStatus = true;
        Ensure();
        var overlay = _instance;
        if (overlay == null) return;

        Callable.From(() =>
        {
            // _Ready may not have run yet when this fires right after the first Ensure().
            if (!GodotObject.IsInstanceValid(overlay) || overlay._kindLabel == null) return;
            overlay._kindLabel.Text = "SpireBot";
            overlay._actionLabel.Text = message;
            foreach (Node child in overlay._topKBox.GetChildren())
                child.QueueFree();
            // Visibility stays under SpireBotConfig.ShowOverlay (_Process reasserts it anyway);
            // the same message is always on the log for an overlay-off run.
        }).CallDeferred();
    }

    /// <summary>
    /// Clears the decision display and hides the panel — called from
    /// <see cref="BotController.Stop"/> so the overlay never keeps exhibiting the last
    /// decision of a run that is over (2026-08-20 UX pacing spec §4). The node itself stays
    /// alive and subscribed; the next run's attach repopulates it fresh. Safe from any
    /// thread — the UI writes are deferred like every other mutation in this class.
    /// </summary>
    public static void OnRunEnded()
    {
        _showingStatus = false;
        var overlay = _instance;
        if (overlay == null) return;

        Callable.From(() =>
        {
            // _Ready may not have run yet if Stop raced the very first Ensure().
            if (!GodotObject.IsInstanceValid(overlay) || overlay._kindLabel == null) return;
            overlay._kindLabel.Text = "Decision: <none yet>";
            overlay._actionLabel.Text = "Chosen: <none yet>";
            foreach (Node child in overlay._topKBox.GetChildren())
                child.QueueFree();
            overlay.UpdateVisibility();
        }).CallDeferred();
    }

    public override void _Ready()
    {
        Name = "SpireBotThinkingOverlay";
        Layer = 100; // draw above the game's own UI layers

        var panel = new PanelContainer
        {
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 0, AnchorBottom = 0,
            // Top offset clears the game's top-of-screen player strip (HP/gold/potions and the
            // relic row) so the overlay never covers the relics.
            OffsetLeft = 8, OffsetTop = 200,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.6f),
            ContentMarginLeft = 8, ContentMarginRight = 8,
            ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer();
        panel.AddChild(vbox);

        _controlBar = new BotControlBar();
        _kindLabel = MakeLabel("Decision: <none yet>");
        _actionLabel = MakeLabel("Chosen: <none yet>");
        _topKBox = new VBoxContainer();

        // First row, above the decision text — same placement as RunReplays' control bar.
        vbox.AddChild(_controlBar);
        vbox.AddChild(_kindLabel);
        vbox.AddChild(_actionLabel);
        vbox.AddChild(_topKBox);

        AddChild(panel);

        UpdateVisibility();
    }

    private static Label MakeLabel(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 14);
        label.AddThemeColorOverride("font_color", Colors.White);
        return label;
    }

    private static void OnDecisionMade(DecisionContext ctx, PolicyResult result)
    {
        Ensure();

        string kind = ctx.Kind.ToString();
        string bestLabel = result.TopK.Length > 0 ? result.TopK[0].label : result.ActionId.ToString();
        string actionLabel = result.ActionId >= 0
            ? $"#{result.ActionId} {bestLabel}"
            : "<no legal action>";
        var topK = result.TopK;

        var instance = _instance;
        if (instance == null) return;
        Callable.From(() => instance.ApplyDecision(kind, actionLabel, topK)).CallDeferred();
    }

    private void ApplyDecision(string kind, string actionLabel, (string label, float prob)[] topK)
    {
        if (!GodotObject.IsInstanceValid(this)) return;

        _showingStatus = false;   // a live decision supersedes any stale status message
        _kindLabel.Text = $"Decision: {kind}";
        _actionLabel.Text = $"Chosen: {actionLabel}";

        foreach (Node child in _topKBox.GetChildren())
            child.QueueFree();

        foreach (var (label, prob) in topK)
        {
            var row = new HBoxContainer();

            var bar = new ProgressBar
            {
                MinValue = 0, MaxValue = 1, Value = prob,
                ShowPercentage = false,
                CustomMinimumSize = new Vector2(100, 12),
            };
            var rowLabel = MakeLabel($" {prob * 100f:0.0}% {label}");

            row.AddChild(bar);
            row.AddChild(rowLabel);
            _topKBox.AddChild(row);
        }

        UpdateVisibility();
    }

    public override void _Process(double delta)
    {
        UpdateVisibility();
        // Driven from _Process rather than from BotController.DecisionMade: that event can fire
        // from dispatch-signal context (see this class's header comment), whereas _Process is
        // always the main thread, so the bar needs no CallDeferred juggling.
        _controlBar.RefreshControls();

        // Paused action preview (paused-action-preview spec, Component 4): while an action is
        // held, the action line reads as a forecast. Sourced from the cache rather than
        // DecisionMade because HandleUnsupported's two paths never fire that event — relabeling
        // in ApplyDecision would leave them previewing an action the overlay never showed.
        // _Process runs after any deferred ApplyDecision, so while a preview is pending this
        // wins; once it clears (commit or unpause), the label falls back to whatever
        // ApplyDecision last wrote — after a commit that is the same action, now accurately
        // labeled "Chosen:".
        string? preview = BotController.PendingPreviewDescription;
        if (preview != null)
            _actionLabel.Text = $"Will take: {preview}";
    }

    private void UpdateVisibility()
    {
        // Attached-or-status, not just the config flag (2026-08-20 UX pacing spec §4): the
        // overlay exists for the run being advised/driven; after Stop it must disappear
        // rather than exhibit the last decision of a run that is over.
        Visible = SpireBotConfig.ShowOverlay && (BotController.IsRunning || _showingStatus);
    }
}
