using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace NmsFrigateFeedOutcomes.Core;

public sealed record FoodOutcome(
    string FoodId,
    string NameKey,
    string Seed,
    string Traits,
    int Combat,
    int Trading,
    int Exploration,
    int Mining,
    int ExpeditionDuration,
    int ExpeditionFuelCost,
    int OverallScore,
    string BestAt,
    bool IsBestAtAnyModifier,
    bool IsBestOverall,
    IReadOnlyList<string> TraitIds,
    string FoodName,
    string LocalizedTraits);

public sealed record TraitDetail(string Name, string StatBoost);

public sealed class FeedPredictor
{
    private const ulong FeedMultiplier = unchecked(0UL - 0x622015F714C7D297UL);
    private const ulong RandomFinalMultiplier = unchecked(0UL - 0x1C955A39EC9ED669UL);
    private const uint RandomWordMultiplier = 0x5A76F899;
    private const ulong RandomMixMultiplier = 0x64DD81482CBD31D7;
    private static readonly string[] PrimaryTraits =
    [
        "LIVING_COM_PRI",
        "LIVING_EXP_PRI",
        "LIVING_MIN_PRI",
        "LIVING_TRA_PRI",
        "LIVING_FUEL_PRI",
        "LIVING_SPE_PRI"
    ];

    private static readonly string[] TraitClassNames =
    [
        "Combat", "Exploration", "Mining", "Diplomacy", "Support", "Normandy",
        "DeepSpace", "DeepSpaceCommon", "Pirate", "GhostShip", "Swarm"
    ];

    private readonly List<Food> _foods;
    private readonly List<Trait> _traits;
    private readonly Dictionary<string, StatModifier> _statModifiers;
    private readonly Dictionary<string, Trait> _traitsById;
    private readonly Dictionary<string, string> _localizedNames;
    private readonly Dictionary<string, string> _localizedTraitNames;

    public FeedPredictor()
    {
        _foods = ReadFoods(LoadXml("NmsFrigateFeedOutcomes.products.MXML"));
        _traits = ReadTraits(LoadXml("NmsFrigateFeedOutcomes.traits.MXML"));
        _statModifiers = ReadStatModifiers(LoadXml("NmsFrigateFeedOutcomes.globals.MXML"));
        _traitsById = _traits.ToDictionary(trait => trait.Id, StringComparer.Ordinal);
        _localizedNames = ReadLocalizedNames();
        _localizedTraitNames = BuildLocalizedTraitNames();
    }

    public IReadOnlyList<FoodOutcome> Calculate(ulong resourceSeed, int traitSlots)
    {
        if (traitSlots is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(traitSlots), "Trait slot count must be between 0 and 5.");
        }

        var results = new List<FoodOutcome>(_foods.Count);

        foreach (var food in _foods)
        {
            var seed = FeedSeedCandidate(PackedFoodKey(food.Id), resourceSeed);
            IReadOnlyList<string> predictedTraits = traitSlots > 0
                ? PredictDeepSpaceCommonTraits(seed, traitSlots)
                : Array.Empty<string>();
            var combat = SumModifier(predictedTraits, "Combat");
            var trading = SumModifier(predictedTraits, "Diplomatic");
            var exploration = SumModifier(predictedTraits, "Exploration");
            var mining = SumModifier(predictedTraits, "Mining");
            var duration = SumModifier(predictedTraits, "Speed");
            var fuelCost = SumModifier(predictedTraits, "FuelCapacity");
            results.Add(new FoodOutcome(
                food.Id,
                food.NameKey,
                $"0x{seed:X16}",
                string.Join(" | ", predictedTraits),
                combat,
                trading,
                exploration,
                mining,
                duration,
                fuelCost,
                combat + trading + exploration + mining - duration - fuelCost,
                "",
                false,
                false,
                predictedTraits,
                GetLocalizedName(food.NameKey, food.Id),
                string.Join(" | ", predictedTraits.Select(id => _localizedTraitNames.GetValueOrDefault(id, id)))));
        }

        return RankOutcomes(results);
    }

    public IReadOnlyList<TraitDetail> DescribeTraits(IEnumerable<string> traitIds)
    {
        return traitIds
            .Select(traitId => traitId.Trim().TrimStart('^'))
            .Where(traitId => traitId.Length > 0)
            .Select(traitId =>
        {
            var found = _traitsById.TryGetValue(traitId, out var trait);
            if (!found && traitId.StartsWith("LIVING_", StringComparison.Ordinal))
            {
                found = _traitsById.TryGetValue(traitId["LIVING_".Length..], out trait);
            }

            if (!found || trait is null)
            {
                return new TraitDetail(_localizedTraitNames.GetValueOrDefault(traitId, traitId), "Stat boost unavailable");
            }

            var statName = trait.Stat switch
            {
                "Diplomatic" => "Trading",
                "Speed" => "Expedition duration",
                "FuelCapacity" => "Expedition fuel cost",
                _ => trait.Stat
            };
            var value = GetModifierValue(trait);
            var unit = trait.Stat == "Speed" ? "%" : "";
            return new TraitDetail(
                GetLocalizedName(trait.NameKey, trait.Id),
                value == 0 ? "No stat change" : $"{statName} {value:+#;-#;0}{unit}");
            }).ToArray();
    }

    public static ulong ParseSeed(string value)
    {
        var trimmed = value.Trim();
        var style = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? System.Globalization.NumberStyles.AllowHexSpecifier
            : System.Globalization.NumberStyles.Integer;
        var digits = style == System.Globalization.NumberStyles.AllowHexSpecifier ? trimmed[2..] : trimmed;
        if (digits.Length == 0 || !ulong.TryParse(digits, style, null, out var result))
        {
            throw new ArgumentException("Enter a decimal seed or a 0x-prefixed hexadecimal seed.", nameof(value));
        }

        return result;
    }

    private static XDocument LoadXml(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded data resource not found: {resourceName}");
        return XDocument.Load(stream);
    }

    private static Dictionary<string, string> ReadLocalizedNames()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NmsFrigateFeedOutcomes.localization-en.json")
            ?? throw new InvalidOperationException("Embedded English localization resource is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException("English localization resource is empty.");
    }

    private Dictionary<string, string> BuildLocalizedTraitNames()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var trait in _traits)
        {
            var displayName = GetLocalizedName(trait.NameKey, trait.Id);
            result[trait.Id] = displayName;
            var prefixedId = trait.Id.StartsWith("LIVING_", StringComparison.Ordinal)
                ? trait.Id
                : $"LIVING_{trait.Id}";
            result[prefixedId] = displayName;
        }

        return result;
    }

    private string GetLocalizedName(string key, string fallback)
    {
        if (!_localizedNames.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Trim().ToLowerInvariant());
    }

    private static List<Food> ReadFoods(XDocument document)
    {
        var table = Property(document.Root, "Table")
            ?? throw new InvalidDataException("Product table is missing.");
        return table.Elements("Property")
            .Where(element => (string?)element.Attribute("value") == "GcProductData")
            .Where(element => Value(element, "CookingIngredient").Equals("true", StringComparison.OrdinalIgnoreCase))
            .Where(element => Value(element, "Consumable").Equals("true", StringComparison.OrdinalIgnoreCase))
            .Where(element => Value(element, "WikiCategory").Equals("Cooking", StringComparison.OrdinalIgnoreCase))
            .Where(element => !Value(element, "Subtitle").StartsWith("BAIT_", StringComparison.Ordinal))
            .Select(element => new Food(Value(element, "ID"), Value(element, "Name")))
            .Where(food => food.Id.Length > 0)
            .OrderBy(food => food.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static List<Trait> ReadTraits(XDocument document)
    {
        var traitsParent = Property(document.Root, "Traits")
            ?? throw new InvalidDataException("Frigate trait table is missing.");
        return traitsParent.Elements("Property")
            .Where(element => (string?)element.Attribute("value") == "GcFrigateTraitData")
            .Select(element =>
            {
                var chances = Property(element, "ChanceOfBeingOffered");
                var weights = TraitClassNames
                    .Select(className => (className, node: Property(chances, className)))
                    .Where(item => item.node is not null)
                    .ToDictionary(item => item.className, item => IntValue(item.node), StringComparer.Ordinal);
                return new Trait(
                    Value(element, "ID"),
                    Value(element, "DisplayName"),
                    Value(Property(element, "FrigateStatType"), "FrigateStatType"),
                    Value(Property(element, "Strength"), "FrigateTraitStrength"),
                    weights);
            })
            .Where(trait => trait.Id.Length > 0)
            .ToList();
    }

    private static Dictionary<string, StatModifier> ReadStatModifiers(XDocument document)
    {
        var strengthTable = Property(Property(document.Root, "FrigateTraitStrengths"), "FrigateStatType")
            ?? throw new InvalidDataException("Frigate trait modifier table is missing.");
        var result = new Dictionary<string, StatModifier>(StringComparer.Ordinal);
        foreach (var stat in strengthTable.Elements("Property"))
        {
            var statType = (string?)stat.Attribute("name") ?? "";
            var alterations = Property(stat, "StatAlteration");
            if (statType.Length == 0 || alterations is null)
            {
                continue;
            }

            result[statType] = new StatModifier(
                Value(stat, "StatLocID"),
                Value(stat, "StatDisplaysPositive").Equals("true", StringComparison.OrdinalIgnoreCase),
                alterations.Elements("Property").ToDictionary(
                    element => (string?)element.Attribute("name") ?? "",
                    IntValue,
                    StringComparer.Ordinal));
        }

        return result;
    }

    private IReadOnlyList<string> PredictDeepSpaceCommonTraits(ulong seed, int traitSlots)
    {
        var (primary, state) = DeepSpaceCommonPrimarySelection(seed);
        var selected = new List<string> { primary };
        var keyHigh = PackedTraitKey(primary).High;

        for (var index = 0; index < Math.Max(0, traitSlots - 1); index++)
        {
            var next = NextTraitRandomPair(state, keyHigh);
            state = next.State;
            keyHigh = next.SecondaryKey;
            var candidate = ChooseWeightedTrait(
                selected,
                next.RandomSeed,
                keyHigh,
                allowNegative: false);
            if (candidate is null)
            {
                break;
            }

            selected.Add(candidate);
        }

        return selected;
    }

    private string? ChooseWeightedTrait(
        IReadOnlyCollection<string> selected,
        ulong randomSeed,
        ulong seedMarker,
        bool allowNegative)
    {
        uint factor;
        uint folded;
        if ((seedMarker & 0xFF) == 0)
        {
            factor = 1;
            folded = 0;
        }
        else
        {
            var low = (uint)randomSeed;
            factor = low + (low == 0 ? 1U : 0U);
            folded = ((low >> 16) | (low << 16)) ^ (uint)(randomSeed >> 32) ^ low;
        }

        var eligible = _traits
            .Where(trait => trait.Weights.GetValueOrDefault("DeepSpaceCommon") > 0)
            .Where(trait => allowNegative || !trait.Strength.StartsWith("Negative", StringComparison.Ordinal))
            .ToList();
        var selectedSet = selected.ToHashSet(StringComparer.Ordinal);
        var remainingWeight = eligible.Sum(trait => trait.Weights["DeepSpaceCommon"])
            - eligible.Where(trait => selectedSet.Contains(trait.Id)).Sum(trait => trait.Weights["DeepSpaceCommon"]);
        if (remainingWeight <= 0)
        {
            return null;
        }

        var scaledSeed = unchecked(factor * RandomWordMultiplier + folded);
        var target = (ulong)scaledSeed * (uint)remainingWeight >> 32;
        foreach (var trait in eligible)
        {
            if (selectedSet.Contains(trait.Id))
            {
                continue;
            }

            var weight = (uint)trait.Weights["DeepSpaceCommon"];
            if (target < weight)
            {
                return trait.Id;
            }

            target -= weight;
        }

        return null;
    }

    private static (string Trait, ulong State) DeepSpaceCommonPrimarySelection(ulong seed)
    {
        var low = (uint)seed;
        var rotated = (low >> 16) | (low << 16);
        var state = (ulong)(rotated ^ (uint)(seed >> 32) ^ low);
        state = unchecked(state + (ulong)(low == 0 ? 1U : low) * RandomWordMultiplier);
        state = unchecked((ulong)(uint)state * RandomWordMultiplier + (state >> 32));
        var index = (int)(((ulong)(uint)state * (uint)PrimaryTraits.Length) >> 32);
        return (PrimaryTraits[index], state);
    }

    private static (ulong State, ulong RandomSeed, ulong SecondaryKey) NextTraitRandomPair(ulong state, ulong secondaryKey)
    {
        var mixed = unchecked((ulong)(uint)state * RandomWordMultiplier + (state >> 32));
        secondaryKey = ((secondaryKey >> 8) << 8) | 1;
        var low = (uint)mixed;
        state = unchecked((ulong)low * RandomWordMultiplier + (mixed >> 32));
        var randomValue = unchecked((((state & uint.MaxValue) >> 1) ^ ((state << 32) | low)) * RandomMixMultiplier);
        randomValue = unchecked(((randomValue >> 33) ^ randomValue) * RandomFinalMultiplier);
        randomValue = (randomValue >> 33) ^ randomValue;
        return (state, randomValue, secondaryKey);
    }

    private static ulong PackedFoodKey(string itemId)
    {
        Span<byte> bytes = stackalloc byte[8];
        var encoded = Encoding.ASCII.GetBytes(itemId);
        encoded.AsSpan(0, Math.Min(encoded.Length, bytes.Length)).CopyTo(bytes);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static (ulong Low, ulong High) PackedTraitKey(string traitId)
    {
        Span<byte> bytes = stackalloc byte[16];
        var encoded = Encoding.ASCII.GetBytes(traitId);
        encoded.AsSpan(0, Math.Min(encoded.Length, bytes.Length)).CopyTo(bytes);
        return (
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[..8]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]));
    }

    private static ulong FeedSeedCandidate(ulong foodKey, ulong resourceSeed)
    {
        var value = unchecked(foodKey * FeedMultiplier);
        value = unchecked(((value >> 47) ^ value) * FeedMultiplier);
        value = unchecked((value ^ resourceSeed) * FeedMultiplier);
        value = unchecked(((value >> 47) ^ value ^ resourceSeed) * FeedMultiplier);
        return unchecked(((value >> 47) ^ value) * FeedMultiplier);
    }

    private int SumModifier(IEnumerable<string> traitIds, string statType)
    {
        return traitIds
            .Where(_traitsById.ContainsKey)
            .Select(id => _traitsById[id])
            .Where(trait => trait.Stat == statType)
            .Sum(GetModifierValue);
    }

    private int GetModifierValue(Trait trait)
    {
        if (!_statModifiers.TryGetValue(trait.Stat, out var stat)
            || !stat.Alterations.TryGetValue(trait.Strength, out var rawValue))
        {
            return 0;
        }

        return stat.DisplayPositive ? rawValue : -rawValue;
    }

    private static IReadOnlyList<FoodOutcome> RankOutcomes(List<FoodOutcome> outcomes)
    {
        var dimensions = new (string Name, Func<FoodOutcome, int> Value, bool HigherIsBetter)[]
        {
            ("Combat", outcome => outcome.Combat, true),
            ("Trading", outcome => outcome.Trading, true),
            ("Exploration", outcome => outcome.Exploration, true),
            ("Mining", outcome => outcome.Mining, true),
            ("Expedition Duration", outcome => outcome.ExpeditionDuration, false),
            ("Expedition Fuel Cost", outcome => outcome.ExpeditionFuelCost, false)
        };
        var bestAt = outcomes.ToDictionary(outcome => outcome, _ => new List<string>());

        foreach (var dimension in dimensions)
        {
            var bestValue = dimension.HigherIsBetter
                ? outcomes.Max(dimension.Value)
                : outcomes.Min(dimension.Value);
            if (bestValue == 0 && outcomes.All(outcome => dimension.Value(outcome) == 0))
            {
                continue;
            }

            foreach (var outcome in outcomes.Where(outcome => dimension.Value(outcome) == bestValue))
            {
                bestAt[outcome].Add(dimension.Name);
            }
        }

        var bestOverall = outcomes.Max(outcome => outcome.OverallScore);
        return outcomes.Select(outcome => outcome with
        {
            BestAt = string.Join(", ", bestAt[outcome]),
            IsBestAtAnyModifier = bestAt[outcome].Count > 0,
            IsBestOverall = outcome.OverallScore == bestOverall
        }).ToArray();
    }

    private static XElement? Property(XElement? element, string name) =>
        element?.Elements("Property").FirstOrDefault(child => (string?)child.Attribute("name") == name);

    private static string Value(XElement? element, string name, string fallback = "") =>
        (string?)Property(element, name)?.Attribute("value") ?? fallback;

    private static int IntValue(XElement? element) =>
        int.TryParse((string?)element?.Attribute("value"), out var value) ? value : 0;

    private sealed record Food(string Id, string NameKey);
    private sealed record Trait(string Id, string NameKey, string Stat, string Strength, Dictionary<string, int> Weights);
    private sealed record StatModifier(string LocalizationId, bool DisplayPositive, Dictionary<string, int> Alterations);
}