# NMS Frigate Feed Outcomes

A Windows WPF app that predicts living-frigate food outcomes from a save's resource seed. The app filters out bait and non-food curios, shows localized trait predictions and stat rankings, and links selected foods to their pages on [No Man's Sky Recipes](https://nomansskyrecipes.com/).

## Prepare Local Game Data

The app does not bundle game-derived data. On first launch, it finds a local Steam installation and prepares the required files in `%LOCALAPPDATA%\NMS-FrigateFeedOutcomes\GameData`. It downloads pinned extraction tools from GitHub to `%TEMP%` and verifies their SHA-256 hashes. To prepare or refresh the cache manually, run from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Prepare-GameData.ps1
```

Steam detection is automatic. For a different install location, pass its game folder explicitly:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Prepare-GameData.ps1 -GamePath "D:\SteamLibrary\steamapps\common\No Man's Sky"
```

It extracts the product, frigate-trait, and fleet-stat tables plus an English localization subset from the game's PAK archives. Existing cache files are preserved; add `-Force` to replace them after a game update. These files remain local and must not be committed.

## Build and Publish

Requires the .NET 9 SDK on Windows. The C# regression tests need local game data. Prepare it with the command above, then run:

```powershell
dotnet run --project .\windows-app\Tests\NmsFrigateFeedOutcomes.Tests.csproj --configuration Release
```

Publish the self-contained Windows x64 app and its setup script to the root `dist` folder:

```powershell
dotnet publish .\windows-app\App\NmsFrigateFeedOutcomes.App.csproj --configuration Release
```

Keep both `dist\NmsFrigateFeedOutcomes.App.exe` and `dist\Prepare-GameData.ps1` together when distributing the app. See [windows-app/README.md](windows-app/README.md) for app usage and details.