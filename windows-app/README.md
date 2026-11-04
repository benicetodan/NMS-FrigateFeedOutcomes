# Frigate Feed Outcomes for Windows

The WPF application is a standalone Windows x64 executable for living frigates, the only frigate class that can be fed. Building it requires local copies of the game-derived product, frigate-trait, and fleet-stat tables plus `Core\Data\localization-en.json`; these files are excluded from this repository. Once built, the published executable embeds them and does not need adjacent data files. Food and trait labels use an English localization subset; IDs remain searchable, and unknown labels fall back to their IDs.

To extract and prepare these local files from an installed Steam copy of the game, follow the [repository setup instructions](../README.md#prepare-local-game-data). The setup script accepts `-GamePath` when automatic Steam detection is not suitable.

Run the C# regression checks from the repository folder:

```powershell
dotnet run --project .\windows-app\Tests\NmsFrigateFeedOutcomes.Tests.csproj -c Release
```

Publish a self-contained, single-file executable:

```powershell
dotnet publish .\windows-app\App\NmsFrigateFeedOutcomes.App.csproj -c Release
```

The self-contained executable is published to `dist\NmsFrigateFeedOutcomes.App.exe`; each publish reuses this same folder. Choose **Open save...**, select a local No Man's Sky `.hg` or JSON save, and choose a living frigate from the list. The selector shows its custom name when present and includes its resource seed to distinguish names; unnamed frigates are identified by their seed. The app reads the save locally and does not modify it. Living frigates (`DeepSpaceCommon`) with a valid `ResourceSeed` are listed. The selected ship's current traits and individual stat boosts appear below the selector, and its food outcomes calculate automatically.

The grid shows localized food names and predicted trait names, supports sorting by each column, and filters by names or IDs. Selecting a food row displays its predicted traits and individual stat boosts next to the frigate's current traits. Modifier columns show per-stat totals. Gold rows are best for at least one stat; green rows have the highest overall benefit score. That score adds Combat, Trading, Exploration, and Mining, and reverses the signs of Duration and Fuel Cost so reductions count as benefits. The ordered-trait model is covered by the three known examples. The food list is limited to consumable cooking products in the `Cooking` wiki category and excludes bait.