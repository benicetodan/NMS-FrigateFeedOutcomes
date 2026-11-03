# NMS Frigate Feed Outcomes

A Windows WPF app that predicts living-frigate food outcomes from a save's resource seed. The app filters out bait and non-food curios, shows localized trait predictions and stat rankings, and links selected foods to their pages on [No Man's Sky Recipes](https://nomansskyrecipes.com/).

## Build and Publish

Requires the .NET 9 SDK on Windows. The game-derived MXML inputs are not included in this repository. Before building, place locally obtained files at these paths:

- `data/nms_reality_gcproducttable.MXML`
- `assets/metadata/reality/tables/frigatetraittable.MXML`
- `assets/gcfleetglobals.global.MXML`
- `windows-app/Core/Data/localization-en.json`

Run the C# regression tests:

```powershell
dotnet run --project .\windows-app\Tests\NmsFrigateFeedOutcomes.Tests.csproj --configuration Release
```

Publish the self-contained Windows x64 app to the single root `dist` folder:

```powershell
dotnet publish .\windows-app\App\NmsFrigateFeedOutcomes.App.csproj --configuration Release
```

The executable is `dist\NmsFrigateFeedOutcomes.App.exe`. See [windows-app/README.md](windows-app/README.md) for app usage and details.