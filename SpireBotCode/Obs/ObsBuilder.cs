using System;

using Godot;

namespace SpireBot.SpireBotCode.Obs;

/// <summary>Raised when a writer would step outside its own contract-declared segment
/// slice — always a programmer error (a stride/offset bug), never a runtime data
/// condition. Thrown unconditionally (not gated on <c>OS.IsDebugBuild()</c>) because
/// a silently-corrupted neighboring segment is worse than a crash in every build
/// configuration; see <see cref="ObsBuilder.Build"/>'s doc comment for the additional
/// debug-only whole-array self-check this pairs with.</summary>
public sealed class ObsBuilderException : Exception
{
    public ObsBuilderException(string message) : base(message) { }
}

/// <summary>The flat run-schema-sized observation the contract's action/obs space
/// describes: <c>F</c> is <c>contract.FDim</c> floats, <c>I</c> is <c>contract.IDim</c>
/// ints (widened to <c>long</c> so this type can carry either a v7 combat id or a
/// larger future vocab id without a second array shape). Always full run-schema size,
/// even when only the combat block is populated (Task 13 lands the run block) — the
/// combat segments some ancestor policy expects are always at the same offsets.</summary>
public sealed class Obs
{
    public float[] F;
    public long[] I;

    public Obs(int fDim, int iDim)
    {
        F = new float[fDim];
        I = new long[iDim];
    }
}

/// <summary>
/// Builds one flat <see cref="Obs"/> from a captured <see cref="DecisionContext"/>,
/// per the contract's layout (<see cref="Contract.F"/>/<see cref="Contract.I"/> name ->
/// slice lookups — no numeric offsets anywhere in this type or its writers).
///
/// Task 12 implements the COMBAT block (<see cref="CombatObsWriter"/>) — every
/// <c>combat.*</c> segment named in the schema audit
/// (<c>docs/superpowers/specs/2026-08-04-spirebot-schema-audit.md</c>). Task 13 implements
/// every other segment (<see cref="RunObsWriter"/>) — <c>phase</c>/<c>run.*</c>/<c>map*</c>/
/// <c>event.*</c>/<c>shop.*</c>/<c>reward.*</c>/<c>select.*</c>. Outside combat, every
/// <c>combat.*</c> segment is left at the array's zero-fill (matches sts2-rl's own "combat
/// block zeroed when no live CombatState" convention — <c>run_env.py</c> only calls
/// <c>write_combat_obs</c> while <c>self.combat is not None</c>); <c>RunObsWriter.Write</c>
/// runs on every decision regardless.
/// </summary>
public sealed class ObsBuilder
{
    private readonly Contract _contract;
    private readonly SessionState _session;

    public ObsBuilder(Contract contract, SessionState session)
    {
        _contract = contract ?? throw new ArgumentNullException(nameof(contract));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>
    /// Builds the observation for one decision. Never throws for "outside combat" —
    /// that just leaves the combat block zeroed; DOES throw <see cref="ObsBuilderException"/>
    /// (via the writers) if a writer's own bookkeeping is wrong, since that is always a
    /// bug in this code, not a runtime data condition.
    /// </summary>
    public Obs Build(DecisionContext ctx)
    {
        var obs = new Obs(_contract.FDim, _contract.IDim);

        CombatObsWriter.Write(_contract, obs, ctx, _session);
        RunObsWriter.Write(_contract, obs, ctx, _session);

        // NOT OS.IsDebugBuild() — always false in the shipped export_release game.
        if (SpireBotConfig.SelfChecks)
            SelfCheck(obs);

        return obs;
    }

    /// <summary>
    /// Debug-build-only whole-array sanity check (plan Step 3): confirms the arrays this
    /// call produced are exactly contract-sized (always true by construction — <see cref="Obs"/>'s
    /// constructor allocates from <c>contract.FDim</c>/<c>IDim</c> directly — but asserted
    /// here anyway so a future refactor that stops doing that fails loudly in a debug
    /// build rather than silently). The PER-SEGMENT bounds check (a writer never stepping
    /// outside its own declared slice) is enforced unconditionally by every writer's
    /// <c>Put</c>/<c>PutI</c> calls (see <see cref="CombatObsWriter"/>'s doc comment) —
    /// not just under <c>OS.IsDebugBuild()</c> — because a stub-bot run exercises this
    /// path on every decision (<see cref="BotController"/> calls <see cref="Build"/> once
    /// per settle signal once wired), which is exactly the "wire it so a stub-bot run
    /// would exercise it" requirement.
    /// </summary>
    private void SelfCheck(Obs obs)
    {
        if (obs.F.Length != _contract.FDim)
            throw new ObsBuilderException($"ObsBuilder.SelfCheck: F.Length {obs.F.Length} != contract.FDim {_contract.FDim}.");
        if (obs.I.Length != _contract.IDim)
            throw new ObsBuilderException($"ObsBuilder.SelfCheck: I.Length {obs.I.Length} != contract.IDim {_contract.IDim}.");
    }
}
