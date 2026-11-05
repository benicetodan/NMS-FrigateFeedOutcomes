using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NmsFrigateFeedOutcomes.Core;

public sealed record LivingFrigate(
    string DisplayName,
    ulong ResourceSeed,
    IReadOnlyList<string> TraitIds,
    IReadOnlyList<int> Stats)
{
    public int TraitSlots => TraitIds.Count;

    public string SelectionDisplay => DisplayName.StartsWith("0x", StringComparison.Ordinal)
        ? DisplayName
        : $"{DisplayName}  (0x{ResourceSeed:X16})";
}

public sealed record LivingFrigateImport(IReadOnlyList<LivingFrigate> Frigates);

public static class NmsSaveFileReader
{
    private static ReadOnlySpan<byte> ChunkSignature => [0xE5, 0xA1, 0xED, 0xFE];
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private const int MaximumExpandedBytes = 128 * 1024 * 1024;
    private const int MaximumChunkBytes = 4 * 1024 * 1024;

    public static JsonDocument ReadJson(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length == 0 || fileInfo.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Save file is missing, empty, or exceeds the 64 MiB import limit.");
        }

        var file = File.ReadAllBytes(path);
        if (file.Length == 0 || file.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Save file is empty or exceeds the 64 MiB import limit.");
        }

        byte[] jsonBytes;
        if (file.AsSpan().StartsWith(ChunkSignature))
        {
            jsonBytes = DecompressChunks(file);
        }
        else
        {
            jsonBytes = file.AsSpan().TrimEnd((byte)0).ToArray();
        }

        try
        {
            var json = NormalizeHexEscapes(Encoding.UTF8.GetString(jsonBytes));
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The save payload is not readable JSON; this save format may be unsupported.", exception);
        }
    }

    private static string NormalizeHexEscapes(string json)
    {
        var normalized = new StringBuilder(json.Length);
        var inString = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (!inString)
            {
                normalized.Append(character);
                if (character == '"')
                {
                    inString = true;
                }

                continue;
            }

            if (character == '"')
            {
                inString = false;
                normalized.Append(character);
                continue;
            }

            if (character == '\\' && index + 1 < json.Length)
            {
                if (json[index + 1] == 'x'
                    && index + 3 < json.Length
                    && IsHexDigit(json[index + 2])
                    && IsHexDigit(json[index + 3]))
                {
                    normalized.Append("\\u00").Append(json[index + 2]).Append(json[index + 3]);
                    index += 3;
                    continue;
                }

                normalized.Append(character).Append(json[++index]);
                continue;
            }

            normalized.Append(character);
        }

        return normalized.ToString();
    }

    private static bool IsHexDigit(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    public static JsonDocument ReadMappedJson(string path)
    {
        using var source = ReadJson(path);
        using var mappingStream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("NmsFrigateFeedOutcomes.save-key-mapping.json")
            ?? throw new InvalidOperationException("Embedded save-key map is missing.");
        using var mappingDocument = JsonDocument.Parse(mappingStream);
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in mappingDocument.RootElement.GetProperty("Mapping").EnumerateArray())
        {
            mappings[entry.GetProperty("Key").GetString()!] = entry.GetProperty("Value").GetString()!;
        }
        var root = JsonNode.Parse(source.RootElement.GetRawText())
            ?? throw new InvalidDataException("Save JSON root is empty.");
        RestorePropertyNames(root, mappings);
        return JsonDocument.Parse(root.ToJsonString());
    }

    public static LivingFrigateImport ReadLivingFrigates(string path)
    {
        using var document = ReadMappedJson(path);
        var baseContext = GetRequiredProperty(document.RootElement, "BaseContext");
        var playerState = GetRequiredProperty(baseContext, "PlayerStateData");
        var frigates = GetRequiredProperty(playerState, "FleetFrigates");
        if (frigates.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Save field 'FleetFrigates' is not an array.");
        }

        var results = new List<LivingFrigate>();

        foreach (var frigate in frigates.EnumerateArray())
        {
            var classData = GetRequiredProperty(frigate, "FrigateClass");
            var className = GetRequiredProperty(classData, "FrigateClass");
            if (className.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("A frigate class value is not a string.");
            }

            var frigateClass = className.GetString();
            if (frigateClass != "DeepSpaceCommon")
            {
                continue;
            }

            var resourceSeed = ReadGcSeed(GetRequiredProperty(frigate, "ResourceSeed"));

            var traitIdArray = GetRequiredProperty(frigate, "TraitIDs");
            if (traitIdArray.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("A living frigate's TraitIDs field is not an array.");
            }

            var traitIds = traitIdArray.EnumerateArray()
                .Select(traitId => traitId.ValueKind == JsonValueKind.String
                    ? traitId.GetString() ?? ""
                    : throw new InvalidDataException("A living frigate TraitIDs entry is not a string."))
                .ToArray();
            if (traitIds.Length > 5)
            {
                throw new InvalidDataException("A living frigate has more than five TraitIDs in its save record.");
            }

            var stats = ReadFrigateStats(frigate);

            var customName = frigate.TryGetProperty("CustomName", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()?.Trim()
                    : null;
            var displayName = string.IsNullOrWhiteSpace(customName)
                ? $"0x{resourceSeed:X16}"
                : customName;
            results.Add(new LivingFrigate(displayName, resourceSeed, traitIds, stats));
        }

        return new LivingFrigateImport(results);
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidDataException($"Save data is missing the expected '{propertyName}' field.");
        }

        return property;
    }

    private static IReadOnlyList<int> ReadFrigateStats(JsonElement frigate)
    {
        if (!frigate.TryGetProperty("Stats", out var stats))
        {
            return Array.Empty<int>();
        }

        if (stats.ValueKind != JsonValueKind.Array || stats.GetArrayLength() != 11)
        {
            throw new InvalidDataException("A living frigate's Stats field must contain eleven values.");
        }

        return stats.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var stat)
                ? stat
                : throw new InvalidDataException("A living frigate Stats entry is not an integer."))
            .ToArray();
    }

    private static ulong ReadGcSeed(JsonElement element)
    {
        JsonElement seedValue;
        if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 2)
        {
            var fields = element.EnumerateArray().ToArray();
            if (fields[0].ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException("A frigate ResourceSeed use flag is not boolean.");
            }

            seedValue = fields[1];
        }
        else if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("Seed", out seedValue)
            && element.TryGetProperty("UseSeedValue", out var useSeedElement))
        {
            if (useSeedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException("A frigate ResourceSeed use flag is not boolean.");
            }
        }
        else
        {
            throw new InvalidDataException("A frigate ResourceSeed has an unsupported save representation.");
        }

        if (seedValue.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("A frigate ResourceSeed is not a hexadecimal string.");
        }

        try
        {
            return FeedPredictor.ParseSeed(seedValue.GetString() ?? "");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("A frigate ResourceSeed is invalid.", exception);
        }
    }

    private static void RestorePropertyNames(JsonNode node, IReadOnlyDictionary<string, string> mappings)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToList())
            {
                var name = property.Key;
                if (mappings.TryGetValue(name, out var mappedName) && mappedName != name)
                {
                    var value = jsonObject[name];
                    jsonObject.Remove(name);
                    if (jsonObject.ContainsKey(mappedName))
                    {
                        throw new InvalidDataException($"Save key mapping creates a duplicate property: {mappedName}");
                    }

                    jsonObject[mappedName] = value;
                    name = mappedName;
                }

                if (jsonObject[name] is { } child)
                {
                    RestorePropertyNames(child, mappings);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                if (item is not null)
                {
                    RestorePropertyNames(item, mappings);
                }
            }
        }
    }

    private static byte[] DecompressChunks(byte[] file)
    {
        using var output = new MemoryStream();
        var offset = 0;
        var chunkCount = 0;
        while (offset < file.Length)
        {
            if (file.Length - offset < 16 || !file.AsSpan(offset, 4).SequenceEqual(ChunkSignature))
            {
                throw new InvalidDataException($"Invalid compressed save chunk at byte {offset}.");
            }

            var chunkLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset + 4, 4));
            var expandedLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset + 8, 4));
            offset += 16;
            if (chunkLength <= 0 || chunkLength > MaximumChunkBytes || expandedLength <= 0
                || expandedLength > MaximumChunkBytes || chunkLength > file.Length - offset
                || output.Length + expandedLength > MaximumExpandedBytes)
            {
                throw new InvalidDataException("Compressed save chunk has an invalid or excessive size.");
            }

            var expanded = new byte[expandedLength];
            DecodeLz4Block(file.AsSpan(offset, chunkLength), expanded);
            output.Write(expanded);
            offset += chunkLength;
            if (++chunkCount > 4096)
            {
                throw new InvalidDataException("Save file contains too many compressed chunks.");
            }
        }

        return output.ToArray().AsSpan().TrimEnd((byte)0).ToArray();
    }

    private static void DecodeLz4Block(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var sourceOffset = 0;
        var destinationOffset = 0;
        while (sourceOffset < source.Length)
        {
            var token = source[sourceOffset++];
            var literalLength = ReadLength(source, ref sourceOffset, token >> 4);
            if (literalLength > source.Length - sourceOffset || literalLength > destination.Length - destinationOffset)
            {
                throw new InvalidDataException("LZ4 literal exceeds the compressed or expanded chunk boundary.");
            }

            source.Slice(sourceOffset, literalLength).CopyTo(destination[destinationOffset..]);
            sourceOffset += literalLength;
            destinationOffset += literalLength;
            if (sourceOffset == source.Length)
            {
                break;
            }

            if (source.Length - sourceOffset < 2)
            {
                throw new InvalidDataException("LZ4 match offset is truncated.");
            }

            var matchOffset = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(sourceOffset, 2));
            sourceOffset += 2;
            if (matchOffset == 0 || matchOffset > destinationOffset)
            {
                throw new InvalidDataException("LZ4 match offset is invalid.");
            }

            var matchLength = checked(ReadLength(source, ref sourceOffset, token & 0x0F) + 4);
            if (matchLength > destination.Length - destinationOffset)
            {
                throw new InvalidDataException("LZ4 match exceeds the expanded chunk boundary.");
            }

            for (var index = 0; index < matchLength; index++)
            {
                destination[destinationOffset + index] = destination[destinationOffset + index - matchOffset];
            }

            destinationOffset += matchLength;
        }

        if (destinationOffset != destination.Length)
        {
            throw new InvalidDataException("LZ4 chunk expanded to an unexpected length.");
        }
    }

    private static int ReadLength(ReadOnlySpan<byte> source, ref int offset, int initialLength)
    {
        var length = initialLength;
        if (initialLength != 15)
        {
            return length;
        }

        byte next;
        do
        {
            if (offset >= source.Length)
            {
                throw new InvalidDataException("LZ4 extended length is truncated.");
            }

            next = source[offset++];
            length = checked(length + next);
        }
        while (next == byte.MaxValue);

        return length;
    }
}