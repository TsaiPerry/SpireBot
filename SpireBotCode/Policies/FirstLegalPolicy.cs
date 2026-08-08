// See IPolicy.cs's identical alias comment.
using ObsResult = SpireBot.SpireBotCode.Obs.Obs;

namespace SpireBot.SpireBotCode.Policies;

/// <summary>
/// Task 11 stub policy: picks the lowest legal action id. No learning, no game-state
/// reasoning — exists so <see cref="BotController"/> has a trivial, always-available policy to
/// drive the decision loop end-to-end, and now (Task 14) doubles as the fallback path
/// <see cref="OnnxPolicy"/> and <see cref="BotController"/> degrade to on any error.
/// Deterministic given the same <see cref="ActionMap.Mask"/>. Ignores <paramref name="obs"/>
/// entirely — it never needs an observation to pick an action id.
///
/// ONE deliberate exception to "lowest id": end-turn is combat action id 0, so a literal
/// lowest-id rule ends the turn before playing anything and the bot dies in its first fight
/// (observed live — it end-turned repeatedly until the run was over). That fails Task 11's own
/// acceptance, which needs the stub to get through combats and on to the map/event/shop/rest
/// screens it is meant to exercise. So end-turn is chosen only when nothing else is legal —
/// which is also the natural terminator, since running out of energy leaves it as the only
/// legal action.
/// </summary>
public sealed class FirstLegalPolicy : IPolicy
{
    public PolicyResult Choose(DecisionContext ctx, ActionMap map, ObsResult? obs)
    {
        int endTurn = ctx.Kind == DecisionKind.Combat ? map.Contract.Actions.Combat.EndTurn : -1;

        int chosen = -1;
        for (int i = 0; i < map.Mask.Length; i++)
        {
            if (map.Mask[i] && i != endTurn)
            {
                chosen = i;
                break;
            }
        }

        // Nothing but end-turn left (no energy, no playable card) — take it.
        if (chosen < 0 && endTurn >= 0 && endTurn < map.Mask.Length && map.Mask[endTurn])
            chosen = endTurn;

        if (chosen < 0)
        {
            // No legal action at all — ActionMap produced an all-false mask. Callers
            // (BotController's Unsupported-kind fallback) are expected to handle this
            // upstream, but return a well-formed (if useless) result rather than throw.
            return new PolicyResult { ActionId = -1, TopK = System.Array.Empty<(string, float)>() };
        }

        return new PolicyResult
        {
            ActionId = chosen,
            TopK = new[] { (map.LabelFor(chosen), 1.0f) },
        };
    }
}
