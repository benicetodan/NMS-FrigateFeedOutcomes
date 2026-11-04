[CmdletBinding()]
param(
    [string]$GamePath,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Add-ExistingPath {
    param(
        [System.Collections.Generic.List[string]]$Paths,
        [System.Collections.Generic.HashSet[string]]$Seen,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ((Test-Path -LiteralPath $fullPath) -and $Seen.Add($fullPath)) {
        $Paths.Add($fullPath)
    }
}

function Find-GameRoot {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolvedPath = (Resolve-Path -LiteralPath $RequestedPath).Path
        if (Test-Path -LiteralPath (Join-Path $resolvedPath 'GAMEDATA\PCBANKS')) {
            return $resolvedPath
        }

        if ((Split-Path -Leaf $resolvedPath) -ieq 'GAMEDATA') {
            $resolvedPath = Split-Path -Parent $resolvedPath
        }
        elseif ((Split-Path -Leaf $resolvedPath) -ieq 'PCBANKS') {
            $resolvedPath = Split-Path -Parent (Split-Path -Parent $resolvedPath)
        }

        if (Test-Path -LiteralPath (Join-Path $resolvedPath 'GAMEDATA\PCBANKS')) {
            return $resolvedPath
        }

        throw "No Man's Sky game files were not found under '$RequestedPath'. Pass the game root or its GAMEDATA\PCBANKS folder."
    }

    $steamRoots = [System.Collections.Generic.List[string]]::new()
    $seenSteamRoots = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($registryPath in @(
        'HKCU:\Software\Valve\Steam',
        'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam',
        'HKLM:\SOFTWARE\Valve\Steam')) {
        if (Test-Path $registryPath) {
            $properties = Get-ItemProperty $registryPath
            foreach ($propertyName in @('SteamPath', 'InstallPath')) {
                Add-ExistingPath $steamRoots $seenSteamRoots ([string]$properties.$propertyName)
            }
        }
    }

    foreach ($defaultPath in @(
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Steam'),
        (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Steam'))) {
        Add-ExistingPath $steamRoots $seenSteamRoots $defaultPath
    }

    foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
        foreach ($candidateName in @('Steam', 'SteamLibrary')) {
            Add-ExistingPath $steamRoots $seenSteamRoots (Join-Path $drive.Root $candidateName)
        }

        Get-ChildItem -LiteralPath $drive.Root -Directory -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'Steam' } |
            ForEach-Object { Add-ExistingPath $steamRoots $seenSteamRoots $_.FullName }
    }

    $libraries = [System.Collections.Generic.List[string]]::new()
    $seenLibraries = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($steamRoot in $steamRoots) {
        Add-ExistingPath $libraries $seenLibraries $steamRoot
        $libraryFoldersFile = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $libraryFoldersFile)) {
            continue
        }

        $vdf = Get-Content -LiteralPath $libraryFoldersFile -Raw
        foreach ($match in [regex]::Matches($vdf, '"path"\s+"(?<path>[^"]+)"')) {
            $libraryPath = $match.Groups['path'].Value.Replace('\\', '\')
            Add-ExistingPath $libraries $seenLibraries $libraryPath
        }
    }

    foreach ($library in $libraries) {
        $candidate = Join-Path $library 'steamapps\common\No Man''s Sky'
        if (Test-Path -LiteralPath (Join-Path $candidate 'GAMEDATA\PCBANKS')) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "No Steam installation of No Man's Sky was found. Rerun with -GamePath 'C:\path\to\No Man''s Sky'."
}

function Get-VerifiedDownload {
    param(
        [string]$Uri,
        [string]$Path,
        [string]$ExpectedSha256
    )

    if (Test-Path -LiteralPath $Path) {
        $existingHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existingHash -eq $ExpectedSha256) {
            return
        }

        Remove-Item -LiteralPath $Path -Force
    }

    $partialPath = "$Path.download"
    Invoke-WebRequest -Uri $Uri -OutFile $partialPath -UseBasicParsing
    $actualHash = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256) {
        Remove-Item -LiteralPath $partialPath -Force
        throw "SHA-256 verification failed for '$Uri'."
    }

    Move-Item -LiteralPath $partialPath -Destination $Path -Force
}

function Read-Mxml {
    param([string]$Path)

    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $document = [System.Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create($Path, $settings)
    try {
        $document.Load($reader)
    }
    finally {
        $reader.Dispose()
    }

    return ,$document
}

function Get-MxmlValue {
    param(
        [System.Xml.XmlNode]$Node,
        [string]$Name
    )

    $property = $Node.SelectSingleNode("Property[@name='$Name']")
    if ($null -eq $property) {
        return ''
    }

    return $property.GetAttribute('value')
}

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$outputFiles = @(
    'data\nms_reality_gcproducttable.MXML',
    'assets\metadata\reality\tables\frigatetraittable.MXML',
    'assets\gcfleetglobals.global.MXML',
    'windows-app\Core\Data\localization-en.json')
$existingFiles = @($outputFiles | Where-Object { Test-Path -LiteralPath (Join-Path $OutputRoot $_) })
if ($existingFiles.Count -gt 0 -and -not $Force) {
    throw "Local game-data files already exist. Review them, then rerun with -Force to replace: $($existingFiles -join ', ')"
}

$resolvedGameRoot = Find-GameRoot $GamePath
$pakDirectory = Join-Path $resolvedGameRoot 'GAMEDATA\PCBANKS'
if (-not (Get-ChildItem -LiteralPath $pakDirectory -File -Filter '*.pak' | Select-Object -First 1)) {
    throw "No .pak archives were found in '$pakDirectory'."
}

$toolCache = Join-Path $env:TEMP 'NMS-FrigateFeedOutcomes\setup-tools'
New-Item -ItemType Directory -Force -Path $toolCache | Out-Null
$workRoot = Join-Path $env:TEMP "NMS-FrigateFeedOutcomes-setup-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null

try {
    $pakZip = Join-Path $toolCache 'hgpaktool-1.1.3-windows.zip'
    Get-VerifiedDownload `
        'https://github.com/monkeyman192/HGPAKtool/releases/download/1.1.3/hgpaktool-x86_64-pc-windows.zip' `
        $pakZip `
        '1c3753e227ba79c7dab9b93777855c15532bc7eb3a2e9dc53daa6e8f6ce0c6fb'

    $pakTool = Join-Path $toolCache 'hgpaktool.exe'
    if (-not (Test-Path -LiteralPath $pakTool)) {
        $unpackRoot = Join-Path $toolCache 'hgpaktool-unpack'
        if (Test-Path -LiteralPath $unpackRoot) {
            Remove-Item -LiteralPath $unpackRoot -Recurse -Force
        }
        Expand-Archive -LiteralPath $pakZip -DestinationPath $unpackRoot
        $downloadedPakTool = Get-ChildItem -LiteralPath $unpackRoot -Recurse -File -Filter 'hgpaktool.exe' |
            Select-Object -First 1
        if ($null -eq $downloadedPakTool) {
            throw 'The verified HGPAKtool release did not contain hgpaktool.exe.'
        }
        Copy-Item -LiteralPath $downloadedPakTool.FullName -Destination $pakTool
        Remove-Item -LiteralPath $unpackRoot -Recurse -Force
    }

    $mbinCompiler = Join-Path $toolCache 'MBINCompiler.exe'
    Get-VerifiedDownload `
        'https://github.com/monkeyman192/MBINCompiler/releases/download/v7.10.0-pre1/MBINCompiler.exe' `
        $mbinCompiler `
        '035c5645753be3cced709b9f1bdcd73d25ea9c5e2f06a438dba45a38d11f9b41'

    $extractRoot = Join-Path $workRoot 'extracted'
    $decodedRoot = Join-Path $workRoot 'decoded'
    $pakArguments = @(
        '-U', '--upper', '-O', $extractRoot,
        '-f', '*NMS_REALITY_GCPRODUCTTABLE.MBIN',
        '-f', '*FRIGATETRAITTABLE.MBIN',
        '-f', '*GCFLEETGLOBALS.GLOBAL.MBIN',
        '-f', '*USENGLISH.MBIN',
        $pakDirectory)
    & $pakTool @pakArguments
    if ($LASTEXITCODE -ne 0) {
        throw "HGPAKtool failed with exit code $LASTEXITCODE."
    }

    $mbinFiles = @(Get-ChildItem -LiteralPath $extractRoot -Recurse -File -Filter '*.MBIN')
    $requiredMbinNames = @(
        'NMS_REALITY_GCPRODUCTTABLE.MBIN',
        'FRIGATETRAITTABLE.MBIN',
        'GCFLEETGLOBALS.GLOBAL.MBIN')
    foreach ($requiredName in $requiredMbinNames) {
        if (-not ($mbinFiles | Where-Object Name -IEQ $requiredName | Select-Object -First 1)) {
            throw "The installed PAK archives did not contain the required table '$requiredName'."
        }
    }
    $languageFiles = @($mbinFiles | Where-Object Name -Like '*USENGLISH.MBIN')
    if ($languageFiles.Count -eq 0) {
        throw 'No US English localization MBIN files were found in the installed PAK archives.'
    }

    New-Item -ItemType Directory -Force -Path $decodedRoot | Out-Null
    foreach ($mbinFile in $mbinFiles) {
        & $mbinCompiler "-d$decodedRoot" $mbinFile.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "MBINCompiler failed for '$($mbinFile.Name)' with exit code $LASTEXITCODE."
        }
    }

    $decodedFiles = @(Get-ChildItem -LiteralPath $decodedRoot -File -Filter '*.MXML')
    $productPath = $decodedFiles | Where-Object Name -IEQ 'NMS_REALITY_GCPRODUCTTABLE.MXML' | Select-Object -First 1 -ExpandProperty FullName
    $traitPath = $decodedFiles | Where-Object Name -IEQ 'FRIGATETRAITTABLE.MXML' | Select-Object -First 1 -ExpandProperty FullName
    $globalsPath = $decodedFiles | Where-Object Name -IEQ 'GCFLEETGLOBALS.GLOBAL.MXML' | Select-Object -First 1 -ExpandProperty FullName
    if (-not $productPath -or -not $traitPath -or -not $globalsPath) {
        throw 'MBINCompiler did not produce all three required MXML tables.'
    }

    $productDocument = Read-Mxml $productPath
    $traitDocument = Read-Mxml $traitPath
    $requiredKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $feedableFoodCount = 0
    foreach ($item in $productDocument.SelectNodes("//Property[@value='GcProductData']")) {
        $subtitle = Get-MxmlValue $item 'Subtitle'
        $isCookingIngredient = (Get-MxmlValue $item 'CookingIngredient') -ieq 'true'
        $isConsumable = (Get-MxmlValue $item 'Consumable') -ieq 'true'
        $wikiCategory = Get-MxmlValue $item 'WikiCategory'
        if (-not $isCookingIngredient -or -not $isConsumable -or $wikiCategory -ine 'Cooking' -or $subtitle.StartsWith('BAIT_', [System.StringComparison]::Ordinal)) {
            continue
        }

        $nameKey = Get-MxmlValue $item 'Name'
        if (-not [string]::IsNullOrWhiteSpace($nameKey)) {
            [void]$requiredKeys.Add($nameKey)
            $feedableFoodCount++
        }
    }
    foreach ($trait in $traitDocument.SelectNodes("//Property[@value='GcFrigateTraitData']")) {
        $nameKey = Get-MxmlValue $trait 'DisplayName'
        if (-not [string]::IsNullOrWhiteSpace($nameKey)) {
            [void]$requiredKeys.Add($nameKey)
        }
    }
    if ($feedableFoodCount -eq 0 -or $requiredKeys.Count -eq 0) {
        throw 'The decoded product or trait tables did not contain the expected records.'
    }

    $localizedValues = @{}
    foreach ($languageFile in ($languageFiles | Sort-Object Name)) {
        $languageMxmlName = [System.IO.Path]::GetFileNameWithoutExtension($languageFile.Name) + '.MXML'
        $languageMxmlPath = $decodedFiles | Where-Object Name -IEQ $languageMxmlName | Select-Object -First 1 -ExpandProperty FullName
        if (-not $languageMxmlPath) {
            throw "MBINCompiler did not produce '$languageMxmlName'."
        }

        $languageDocument = Read-Mxml $languageMxmlPath
        foreach ($entry in $languageDocument.SelectNodes("//Property[@value='TkLocalisationEntry']")) {
            $id = Get-MxmlValue $entry 'Id'
            if (-not $requiredKeys.Contains($id)) {
                continue
            }

            $value = Get-MxmlValue $entry 'USEnglish'
            if ([string]::IsNullOrWhiteSpace($value)) {
                $value = Get-MxmlValue $entry 'English'
            }
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $localizedValues[$id] = $value
            }
        }
    }

    foreach ($representativeKey in @('FOOD_PIE_MUSH_NAME', 'FOOD_R_DOUGH_NAME')) {
        if (-not $localizedValues.ContainsKey($representativeKey)) {
            throw "The English localization tables are missing the expected key '$representativeKey'."
        }
    }

    $stagingRoot = Join-Path $workRoot 'ready'
    $stagedProduct = Join-Path $stagingRoot 'data\nms_reality_gcproducttable.MXML'
    $stagedTraits = Join-Path $stagingRoot 'assets\metadata\reality\tables\frigatetraittable.MXML'
    $stagedGlobals = Join-Path $stagingRoot 'assets\gcfleetglobals.global.MXML'
    $stagedLocalization = Join-Path $stagingRoot 'windows-app\Core\Data\localization-en.json'
    foreach ($file in @($stagedProduct, $stagedTraits, $stagedGlobals, $stagedLocalization)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null
    }
    Copy-Item -LiteralPath $productPath -Destination $stagedProduct
    Copy-Item -LiteralPath $traitPath -Destination $stagedTraits
    Copy-Item -LiteralPath $globalsPath -Destination $stagedGlobals

    $orderedLocalization = [ordered]@{}
    foreach ($key in @($requiredKeys | Sort-Object)) {
        if ($localizedValues.ContainsKey($key)) {
            $orderedLocalization[$key] = $localizedValues[$key]
        }
    }
    $json = ConvertTo-Json -InputObject $orderedLocalization -Depth 3
    [System.IO.File]::WriteAllText(
        $stagedLocalization,
        $json,
        [System.Text.UTF8Encoding]::new($false))

    foreach ($relativePath in $outputFiles) {
        $source = Join-Path $stagingRoot $relativePath
        $destination = Join-Path $OutputRoot $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }

    Write-Host "Prepared local game data from '$resolvedGameRoot'."
    Write-Host "Food products: $feedableFoodCount; localized product/trait keys: $($orderedLocalization.Count)."
    Write-Host "Output: $OutputRoot (these files are ignored by Git)."
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}