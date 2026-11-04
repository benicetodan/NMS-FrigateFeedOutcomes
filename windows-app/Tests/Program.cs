using NmsFrigateFeedOutcomes.Core;
using System.Text.Json;

if (args.Length == 2 && args[0] == "--inspect-save")
{
    var imported = NmsSaveFileReader.ReadLivingFrigates(args[1]);
    foreach (var frigate in imported.Frigates)
    {
        if (frigate.TraitSlots is < 0 or > 5)
        {
            throw new InvalidOperationException("Imported trait slot count is outside 0 to 5.");
        }
    }

    Console.WriteLine($"Imported {imported.Frigates.Count} living frigate(s).");

    return;
}

var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var localCacheRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "NMS-FrigateFeedOutcomes",
    "GameData");
var cacheHasGameData = new[]
{
    Path.Combine("data", "nms_reality_gcproducttable.MXML"),
    Path.Combine("assets", "metadata", "reality", "tables", "frigatetraittable.MXML"),
    Path.Combine("assets", "gcfleetglobals.global.MXML"),
    Path.Combine("windows-app", "Core", "Data", "localization-en.json")
}.All(relativePath => File.Exists(Path.Combine(localCacheRoot, relativePath)));
var gameDataRoot = cacheHasGameData
    ? localCacheRoot
    : repositoryRoot;
var predictor = new FeedPredictor(gameDataRoot);
const ulong resourceSeed = 0xBF577529BF2690DF;
var outcomes = predictor.Calculate(resourceSeed, 5);
if (outcomes.Any(outcome => outcome.FoodId.StartsWith("BAIT_", StringComparison.Ordinal)))
{
    throw new InvalidOperationException("Bait products must not be listed as living-frigate food.");
}

if (outcomes.Any(outcome => outcome.FoodId == "BAIT_BASIC" || outcome.FoodId == "FIENDCORE")
    || !outcomes.Any(outcome => outcome.FoodId == "FOOD_P_CAVE")
    || !outcomes.Any(outcome => outcome.FoodId == "FOOD_R_DOUGH"))
{
    throw new InvalidOperationException("Food eligibility filtering included bait/Larval Core or excluded known feedable foods incorrectly.");
}

var cases = new[]
{
    (Food: "FOOD_P_CAVE", Seed: "0xBA8751B265C818F9", Traits: new[] { "LIVING_TRA_PRI", "LIVING_TRA_T_3", "LIVING_FUEL_S_5", "LIVING_COM_S_6", "LIVING_SPE_T_1" }),
    (Food: "FOOD_R_DOUGH", Seed: "0x89C0E44DA8EF5EE0", Traits: new[] { "LIVING_SPE_PRI", "LIVING_MIN_S_1", "LIVING_SPE_T_3", "LIVING_SPE_T_1", "LIVING_FUEL_T_1" }),
    (Food: "FOOD_R_HORROR", Seed: "0x59A08F58BED28776", Traits: new[] { "LIVING_FUEL_PRI", "LIVING_COM_T_1", "LIVING_SPE_S_3", "LIVING_TRA_T_1", "LIVING_FUEL_S_5" })
};

foreach (var testCase in cases)
{
    var actual = outcomes.Single(result => result.FoodId == testCase.Food);
    if (actual.Seed != testCase.Seed || !actual.TraitIds.SequenceEqual(testCase.Traits))
    {
        throw new InvalidOperationException($"Regression mismatch for {testCase.Food}: {actual.Seed} [{actual.Traits}]");
    }
}
var fungalTart = outcomes.Single(result => result.FoodId == "FOOD_PIE_MUSH");
if (fungalTart.FoodName != "Fungal Tart")
{
    throw new InvalidOperationException($"Food localization mismatch for {fungalTart.FoodId}: {fungalTart.FoodName}");
}

var dough = outcomes.Single(result => result.FoodId == "FOOD_R_DOUGH");
if (!dough.LocalizedTraits.StartsWith("Wormhole Navigation", StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Trait localization mismatch: {dough.LocalizedTraits}");
}

var oneSlotOutcomes = predictor.Calculate(resourceSeed, 1);
foreach (var outcome in oneSlotOutcomes)
{
    if (outcome.TraitIds.Count != 1)
    {
        throw new InvalidOperationException($"One trait slot should yield one predicted trait for {outcome.FoodId}.");
    }
}

try
{
    predictor.Calculate(resourceSeed, 6);
    throw new InvalidOperationException("Six trait slots should have been rejected.");
}
catch (ArgumentOutOfRangeException)
{
}

var horror = outcomes.Single(result => result.FoodId == "FOOD_R_HORROR");
if (horror.Combat != 3
    || horror.Trading != 3
    || horror.Exploration != 0
    || horror.Mining != 0
    || horror.ExpeditionDuration != -2
    || horror.ExpeditionFuelCost != -18
    || horror.OverallScore != 26)
{
    throw new InvalidOperationException($"Modifier total regression mismatch: {horror}");
}

AssertRanking("Combat", outcome => outcome.Combat, higherIsBetter: true);
AssertRanking("Trading", outcome => outcome.Trading, higherIsBetter: true);
AssertRanking("Exploration", outcome => outcome.Exploration, higherIsBetter: true);
AssertRanking("Mining", outcome => outcome.Mining, higherIsBetter: true);
AssertRanking("Expedition Duration", outcome => outcome.ExpeditionDuration, higherIsBetter: false);
AssertRanking("Expedition Fuel Cost", outcome => outcome.ExpeditionFuelCost, higherIsBetter: false);
var bestOverall = outcomes.Max(outcome => outcome.OverallScore);
if (outcomes.Any(outcome => outcome.IsBestOverall != (outcome.OverallScore == bestOverall)))
{
    throw new InvalidOperationException("Best-overall highlighting does not match the highest benefit score.");
}

var temporarySavePath = Path.Combine(Path.GetTempPath(), $"nms-save-reader-{Guid.NewGuid():N}.json");
try
{
    File.WriteAllText(temporarySavePath, """{"note":"\x41"}""");
    using var escapedJson = NmsSaveFileReader.ReadJson(temporarySavePath);
    if (escapedJson.RootElement.GetProperty("note").GetString() != "A")
    {
        throw new InvalidOperationException("Hex-escaped JSON strings were not normalized correctly.");
    }

    File.WriteAllText(temporarySavePath, """{"BaseContext":{"PlayerStateData":{"FleetFrigates":[{"FrigateClass":{"FrigateClass":"DeepSpaceCommon"},"ResourceSeed":[false,"0x0000000000000001"],"TraitIDs":[],"CustomName":"Test"}]}}}""");
    var unflaggedSeedImport = NmsSaveFileReader.ReadLivingFrigates(temporarySavePath);
    if (unflaggedSeedImport.Frigates.Count != 1 || unflaggedSeedImport.Frigates[0].ResourceSeed != 1)
    {
        throw new InvalidOperationException("A valid living-frigate seed was ignored because UseSeedValue was false.");
    }

    File.WriteAllText(temporarySavePath, """{"BaseContext":{"PlayerStateData":{"FleetFrigates":[{"FrigateClass":{"FrigateClass":"DeepSpaceCommon"},"ResourceSeed":[true,"0x0000000000000001"],"TraitIDs":["LIVING_SPE_PRI"],"CustomName":"Test"}]}}}""");
    var traitImport = NmsSaveFileReader.ReadLivingFrigates(temporarySavePath).Frigates.Single();
    var currentTrait = predictor.DescribeTraits(traitImport.TraitIds).Single();
    if (traitImport.TraitSlots != 1
        || currentTrait.Name != "Wormhole Navigation"
        || !currentTrait.StatBoost.StartsWith("Expedition duration ", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Current frigate trait details were not imported correctly: {traitImport} {currentTrait}");
    }

    var caretTraits = predictor.DescribeTraits(["^LIVING_SPE_PRI", "^"]);
    if (caretTraits.Count != 1
        || caretTraits[0].Name != "Wormhole Navigation"
        || !caretTraits[0].StatBoost.StartsWith("Expedition duration ", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Caret-prefixed current trait IDs were not normalized for display.");
    }

    File.WriteAllText(temporarySavePath, "{}");
    try
    {
        NmsSaveFileReader.ReadLivingFrigates(temporarySavePath);
        throw new InvalidOperationException("A save without the expected structure should be rejected.");
    }
    catch (InvalidDataException)
    {
    }
}
finally
{
    File.Delete(temporarySavePath);
}

Console.WriteLine("All C# checks passed (3 seeds, 15 ordered traits, modifier totals, and ranking flags).");

void AssertRanking(string name, Func<FoodOutcome, int> value, bool higherIsBetter)
{
    var values = outcomes.Select(value).ToArray();
    var bestValue = higherIsBetter ? values.Max() : values.Min();
    var allZero = values.All(candidate => candidate == 0);
    foreach (var outcome in outcomes)
    {
        var expected = !allZero && value(outcome) == bestValue;
        var actual = outcome.BestAt.Split(", ", StringSplitOptions.RemoveEmptyEntries).Contains(name);
        if (actual != expected)
        {
            throw new InvalidOperationException($"Best-at ranking mismatch for {name} on {outcome.FoodId}.");
        }
    }
}

