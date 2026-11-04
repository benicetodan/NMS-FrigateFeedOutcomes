# NMS Frigate Feed Outcomes

A Windows WPF app that predicts living-frigate food outcomes from a save's resource seed. The app filters out bait and non-food curios, shows localized trait predictions and stat rankings, and links selected foods to their pages on [No Man's Sky Recipes](https://nomansskyrecipes.com/).

## Prepare Local Game Data

Requires a local No Man's Sky installation. On first use, the script downloads pinned extraction tools from GitHub to `%TEMP%` and verifies their SHA-256 hashes. From the repository root, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Prepare-GameData.ps1
```

The script detects a Steam installation automatically. For a different install location, pass its game folder explicitly:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Prepare-GameData.ps1 -GamePath "D:\SteamLibrary\steamapps\common\No Man's Sky"
```

It generates the product, frigate-trait, and fleet-stat MXML tables plus an English localization JSON at the Git-ignored paths required by the app. Existing files are preserved; to replace them, add `-Force`. Keep these game-derived files out of commits.

## Build and Publish

Requires the .NET 9 SDK on Windows. Run the C# regression tests:

Run the C# regression tests:

```powershell
dotnet run --project .\windows-app\Tests\NmsFrigateFeedOutcomes.Tests.csproj --configuration Release
```

Publish the self-contained Windows x64 app to the single root `dist` folder:

```powershell
dotnet publish .\windows-app\App\NmsFrigateFeedOutcomes.App.csproj --configuration Release
```

The executable is `dist\NmsFrigateFeedOutcomes.App.exe`. See [windows-app/README.md](windows-app/README.md) for app usage and details.