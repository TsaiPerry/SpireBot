using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Raised for any problem loading or validating a contract file: missing file, malformed JSON,
/// an unsupported contract_version, a non-contiguous layout, or a dimension mismatch. The mod
/// never falls back silently on a bad contract — a bad contract means the bot cannot run, so
/// every failure surfaces here instead of producing a policy that reads garbage observations.
/// </summary>
public sealed class ContractException : Exception
{
    public ContractException(string message) : base(message) { }
    public ContractException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>One named segment's position within a flat observation vector.</summary>
public readonly record struct LayoutSlice(int Offset, int Width);

/// <summary>Mirrors the contract JSON's "actions" block: the flat action-id layout the policy's output head uses.</summary>
public sealed class CombatActionLayout
{
    public int EndTurn { get; init; }
    public int PlayBase { get; init; }
    public int MaxHand { get; init; }
    public int MaxEnemies { get; init; }
    public int PotionBase { get; init; }
    public int MaxPotions { get; init; }
}

public sealed class ChoiceActionLayout
{
    public int Base { get; init; }
    public int Slots { get; init; }
}

public sealed class SelectActionLayout
{
    public int Base { get; init; }
    public int MaxCandidates { get; init; }
}

public sealed class BeltPotionActionLayout
{
    public int Base { get; init; }
    public int Slots { get; init; }
}

/// <summary>The v22 out-of-combat potion-discard block (contract v2, sts2-rl
/// run_env.DISCARD_BASE): slot p discards run.potions[p] via the belt popup's
/// Discard button. Positional and untargeted, exactly like BeltPotion.</summary>
public sealed class DiscardActionLayout
{
    public int Base { get; init; }
    public int Slots { get; init; }
}

public sealed class ActionLayout
{
    public int NActions { get; init; }
    public CombatActionLayout Combat { get; init; } = new();
    public ChoiceActionLayout Choice { get; init; } = new();
    public SelectActionLayout Select { get; init; } = new();
    public BeltPotionActionLayout BeltPotion { get; init; } = new();
    public DiscardActionLayout Discard { get; init; } = new();
}

/// <summary>
/// Loads and validates the SpireBot contract JSON exported by sts2-rl's
/// <c>sts2_rl.live.export_contract</c> (see that repo's <c>sts2_rl/live/contract.py</c> —
/// the normative spec for this file's shape). The contract is the single source of truth for
/// how a trained policy's flat obs vectors and action ids map onto game concepts: obs layout
/// (named float/int segments), vocab ids (sim content -> stable integer id, 0 = PAD/unknown),
/// the game-save-id -> vocab-id mapping, and the action-head layout.
///
/// This type is intentionally Godot-free (no `Godot.*` references) so it can be exercised in a
/// plain console harness outside the mod runtime; all Godot logging lives in ContractSelfTest.
/// </summary>
public sealed class Contract
{
    public int ContractVersion { get; private init; }
    public int CombatObsSchema { get; private init; }
    public int RunObsSchema { get; private init; }
    public int FDim { get; private init; }
    public int IDim { get; private init; }
    public int NActions { get; private set; }
    public ActionLayout Actions { get; private set; } = new();

    private readonly Dictionary<string, LayoutSlice> _fLayout = new();
    private readonly Dictionary<string, LayoutSlice> _iLayout = new();

    // kind -> (game_id -> vocab id). game_id_map keys are already fully-qualified, e.g. "CARD.STRIKE".
    private readonly Dictionary<string, Dictionary<string, int>> _gameIdMap = new();

    // sim-vocabulary name -> stable id, from contract.json's top-level "vocab" block (e.g.
    // "vocab.purposes": {"exhaust": 7, "upgrade": 14, ...} — see PurposeId). Unlike
    // game_id_map, this is NOT keyed by kind-of-game-content; each vocab table is its own
    // flat name->id dictionary. Optional block — older contract builds without it just leave
    // this empty and every lookup returns 0/PAD, matching the pre-round-3 behavior.
    private readonly Dictionary<string, Dictionary<string, int>> _vocab = new();

    // 2: v22 potion-discard action block ("actions.discard", n_actions 243→253).
    // v1 is NOT accepted: a v1 contract means a pre-discard model whose 243-wide
    // head this build's ActionMap would misread — deploy model+contract+mod as a set.
    private const int SupportedContractVersion = 2;

    private Contract() { }

    public static Contract Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ContractException("Contract.Load: path is null or empty.");

        if (!File.Exists(path))
            throw new ContractException($"Contract.Load: file not found: '{path}'.");

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new ContractException($"Contract.Load: failed to read '{path}': {ex.Message}", ex);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ContractException($"Contract.Load: '{path}' is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            int contractVersion = RequireInt(root, "contract_version", path);
            if (contractVersion != SupportedContractVersion)
            {
                throw new ContractException(
                    $"Contract.Load: unsupported contract_version {contractVersion} in '{path}' " +
                    $"(this build supports {SupportedContractVersion}).");
            }

            int combatObsSchema = RequireInt(root, "combat_obs_schema", path);
            int runObsSchema = RequireInt(root, "run_obs_schema", path);
            int fDim = RequireInt(root, "f_dim", path);
            int iDim = RequireInt(root, "i_dim", path);

            if (!root.TryGetProperty("layout", out var layoutElem))
                throw new ContractException($"Contract.Load: '{path}' is missing required block 'layout'.");
            if (!root.TryGetProperty("game_id_map", out var gameIdMapElem))
                throw new ContractException($"Contract.Load: '{path}' is missing required block 'game_id_map'.");
            if (!root.TryGetProperty("actions", out var actionsElem))
                throw new ContractException($"Contract.Load: '{path}' is missing required block 'actions'.");

            var contract = new Contract
            {
                ContractVersion = contractVersion,
                CombatObsSchema = combatObsSchema,
                RunObsSchema = runObsSchema,
                FDim = fDim,
                IDim = iDim,
            };

            contract.LoadLayoutHalf(layoutElem, "f", fDim, contract._fLayout, path);
            contract.LoadLayoutHalf(layoutElem, "i", iDim, contract._iLayout, path);

            contract.LoadGameIdMap(gameIdMapElem, path);

            // Optional — see _vocab's doc comment. Unlike layout/game_id_map/actions this is
            // not required for a contract to be usable (every existing writer already treats
            // its absence as "PAD everything"), so a missing block is not an error.
            if (root.TryGetProperty("vocab", out var vocabElem) && vocabElem.ValueKind == JsonValueKind.Object)
                contract.LoadVocab(vocabElem, path);

            var actions = contract.LoadActions(actionsElem, path);
            contract.Actions = actions;
            contract.NActions = actions.NActions;

            return contract;
        }
    }

    private static int RequireInt(JsonElement root, string name, string path)
    {
        if (!root.TryGetProperty(name, out var elem) || elem.ValueKind != JsonValueKind.Number)
            throw new ContractException($"Contract.Load: '{path}' is missing required integer field '{name}'.");
        return elem.GetInt32();
    }

    private void LoadLayoutHalf(
        JsonElement layoutElem, string half, int expectedDim,
        Dictionary<string, LayoutSlice> into, string path)
    {
        if (!layoutElem.TryGetProperty(half, out var segsElem) || segsElem.ValueKind != JsonValueKind.Array)
        {
            throw new ContractException(
                $"Contract.Load: '{path}' is missing required layout block 'layout.{half}'.");
        }

        int runningOffset = 0;
        foreach (var seg in segsElem.EnumerateArray())
        {
            if (!seg.TryGetProperty("name", out var nameElem) || nameElem.ValueKind != JsonValueKind.String)
                throw new ContractException($"Contract.Load: 'layout.{half}' has a segment with no 'name' in '{path}'.");
            string name = nameElem.GetString()!;

            if (!seg.TryGetProperty("offset", out var offsetElem) || offsetElem.ValueKind != JsonValueKind.Number)
                throw new ContractException($"Contract.Load: 'layout.{half}' segment '{name}' has no numeric 'offset' in '{path}'.");
            int offset = offsetElem.GetInt32();

            if (!seg.TryGetProperty("width", out var widthElem) || widthElem.ValueKind != JsonValueKind.Number)
                throw new ContractException($"Contract.Load: 'layout.{half}' segment '{name}' has no numeric 'width' in '{path}'.");
            int width = widthElem.GetInt32();

            if (offset != runningOffset)
            {
                throw new ContractException(
                    $"Contract.Load: 'layout.{half}' is non-contiguous at segment '{name}' " +
                    $"(expected offset {runningOffset}, got {offset}) in '{path}'.");
            }

            if (into.ContainsKey(name))
                throw new ContractException($"Contract.Load: 'layout.{half}' has a duplicate segment name '{name}' in '{path}'.");

            into[name] = new LayoutSlice(offset, width);
            runningOffset += width;
        }

        if (runningOffset != expectedDim)
        {
            throw new ContractException(
                $"Contract.Load: 'layout.{half}' total width {runningOffset} does not match " +
                $"{(half == "f" ? "f_dim" : "i_dim")} {expectedDim} in '{path}'.");
        }
    }

    private void LoadGameIdMap(JsonElement gameIdMapElem, string path)
    {
        foreach (var kindProp in gameIdMapElem.EnumerateObject())
        {
            string kind = kindProp.Name;
            if (kindProp.Value.ValueKind != JsonValueKind.Object)
            {
                throw new ContractException(
                    $"Contract.Load: 'game_id_map.{kind}' is not an object in '{path}'.");
            }

            var map = new Dictionary<string, int>();
            foreach (var entry in kindProp.Value.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Number)
                {
                    throw new ContractException(
                        $"Contract.Load: 'game_id_map.{kind}.{entry.Name}' is not a number in '{path}'.");
                }
                map[entry.Name] = entry.Value.GetInt32();
            }
            _gameIdMap[kind] = map;
        }
    }

    private void LoadVocab(JsonElement vocabElem, string path)
    {
        foreach (var tableProp in vocabElem.EnumerateObject())
        {
            string table = tableProp.Name;
            if (tableProp.Value.ValueKind != JsonValueKind.Object)
                continue; // tolerate non-table entries — this block is optional/best-effort

            var map = new Dictionary<string, int>();
            foreach (var entry in tableProp.Value.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Number)
                    continue;
                map[entry.Name] = entry.Value.GetInt32();
            }
            _vocab[table] = map;
        }
    }

    /// <summary>Looks up a name in one of contract.json's "vocab" tables (e.g. table="purposes",
    /// name="exhaust"). Returns 0 (PAD/unknown) when the table or name is absent — same
    /// PAD-on-unmapped convention as <see cref="VocabId"/>, and safe to call even against an
    /// older contract build that has no "vocab" block at all.</summary>
    public int VocabTableId(string table, string name)
    {
        if (!_vocab.TryGetValue(table, out var map))
            return 0;
        if (!map.TryGetValue(name, out var id))
            return 0;
        return id;
    }

    private ActionLayout LoadActions(JsonElement actionsElem, string path)
    {
        int nActions = RequireInt(actionsElem, "n_actions", path);

        var combat = RequireObject(actionsElem, "actions.combat", path);
        var choice = RequireObject(actionsElem, "actions.choice", path);
        var select = RequireObject(actionsElem, "actions.select", path);
        var beltPotion = RequireObject(actionsElem, "actions.belt_potion", path);
        var discard = RequireObject(actionsElem, "actions.discard", path);

        return new ActionLayout
        {
            NActions = nActions,
            Combat = new CombatActionLayout
            {
                EndTurn = RequireInt(combat, "end_turn", path),
                PlayBase = RequireInt(combat, "play_base", path),
                MaxHand = RequireInt(combat, "max_hand", path),
                MaxEnemies = RequireInt(combat, "max_enemies", path),
                PotionBase = RequireInt(combat, "potion_base", path),
                MaxPotions = RequireInt(combat, "max_potions", path),
            },
            Choice = new ChoiceActionLayout
            {
                Base = RequireInt(choice, "base", path),
                Slots = RequireInt(choice, "slots", path),
            },
            Select = new SelectActionLayout
            {
                Base = RequireInt(select, "base", path),
                MaxCandidates = RequireInt(select, "max_candidates", path),
            },
            BeltPotion = new BeltPotionActionLayout
            {
                Base = RequireInt(beltPotion, "base", path),
                Slots = RequireInt(beltPotion, "slots", path),
            },
            Discard = new DiscardActionLayout
            {
                Base = RequireInt(discard, "base", path),
                Slots = RequireInt(discard, "slots", path),
            },
        };
    }

    private static JsonElement RequireObject(JsonElement parent, string dottedName, string path)
    {
        // dottedName is "actions.<key>"; we only need the last segment to index into parent.
        string key = dottedName.Substring(dottedName.LastIndexOf('.') + 1);
        if (!parent.TryGetProperty(key, out var elem) || elem.ValueKind != JsonValueKind.Object)
            throw new ContractException($"Contract.Load: '{path}' is missing required block '{dottedName}'.");
        return elem;
    }

    /// <summary>Look up a named float-half layout segment. Throws if the name is unknown.</summary>
    public LayoutSlice F(string name)
    {
        if (_fLayout.TryGetValue(name, out var slice))
            return slice;
        throw new ContractException($"Contract.F: unknown float layout segment '{name}'.");
    }

    /// <summary>Look up a named int-half layout segment. Throws if the name is unknown.</summary>
    public LayoutSlice I(string name)
    {
        if (_iLayout.TryGetValue(name, out var slice))
            return slice;
        throw new ContractException($"Contract.I: unknown int layout segment '{name}'.");
    }

    /// <summary>
    /// Look up an int-half segment that a contract may legitimately not have, returning false
    /// instead of throwing. For blocks added in a LATER obs schema than the loaded contract: the
    /// writer fills them when present and skips them otherwise, so a newer mod build keeps driving
    /// an older bundled model instead of failing every observation on a missing key. Use
    /// <see cref="I"/> for anything the contract must have — a silent skip there would feed the
    /// policy a zeroed block and look like a normal decision.
    /// </summary>
    public bool TryI(string name, out LayoutSlice slice) => _iLayout.TryGetValue(name, out slice);

    /// <summary>
    /// Maps a fully-qualified game-save id (e.g. "CARD.STRIKE") of the given kind (e.g. "cards")
    /// to its stable vocab id. Returns 0 (PAD) when the kind or id is unmapped — callers treat 0
    /// as "unknown content", never as an error, since save files may reference content this
    /// contract build doesn't know about.
    /// </summary>
    public int VocabId(string kind, string gameModelId)
    {
        if (!_gameIdMap.TryGetValue(kind, out var map))
            return 0;
        if (!map.TryGetValue(gameModelId, out var id))
            return 0;
        return id;
    }
}
