using System.Text.Json;
using VocalB1.Core.Models;

namespace VocalB1.Core.Serialization;

/// <summary>
/// Reads chorus formations from a JSON file. Accepted shapes:
/// <list type="bullet">
/// <item><c>{ "formations": [ { "name": "...", "positions": [ { "x": -3, "y": 1 } ] } ] }</c></item>
/// <item><c>[ { "name": "...", "positions": [ [ -3, 1 ] ] } ]</c></item>
/// <item><c>{ "name": "...", "positions": [ ... ] }</c></item>
/// </list>
/// Positions are meters: X from the stage center, Y from the stage front.
/// </summary>
public static class ChorusFormationJson
{
    private const int MaxPositions = 64;

    public static IReadOnlyList<ChorusFormationPreset> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var result = new List<ChorusFormationPreset>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
                result.Add(ParseOne(element, result.Count + 1));
        }
        else if (root.ValueKind == JsonValueKind.Object
                 && TryGetProperty(root, "formations", out var formations)
                 && formations.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in formations.EnumerateArray())
                result.Add(ParseOne(element, result.Count + 1));
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            result.Add(ParseOne(root, 1));
        }
        else
        {
            throw new InvalidDataException("Formation JSON must be an object or an array.");
        }

        if (result.Count == 0)
            throw new InvalidDataException("No formation found in the JSON file.");

        return result;
    }

    private static ChorusFormationPreset ParseOne(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Formation #{index} is not an object.");

        var name = TryGetProperty(element, "name", out var nameElement)
                   && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(name))
            name = $"导入队形 {index}";

        if (!TryGetProperty(element, "positions", out var positions)
            || positions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Formation \"{name}\" has no positions array.");

        var points = new List<ChorusFormationPoint>();
        foreach (var position in positions.EnumerateArray())
        {
            if (points.Count >= MaxPositions)
                throw new InvalidDataException($"Formation \"{name}\" has more than {MaxPositions} positions.");
            points.Add(ParsePoint(position, name, points.Count + 1));
        }

        if (points.Count == 0)
            throw new InvalidDataException($"Formation \"{name}\" has no positions.");

        return new ChorusFormationPreset
        {
            Name = name.Trim(),
            Positions = points,
        };
    }

    private static ChorusFormationPoint ParsePoint(JsonElement element, string formation, int index)
    {
        double x;
        double y;

        if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() >= 2)
        {
            x = element[0].GetDouble();
            y = element[1].GetDouble();
        }
        else if (element.ValueKind == JsonValueKind.Object
                 && TryGetProperty(element, "x", out var xElement)
                 && TryGetProperty(element, "y", out var yElement))
        {
            x = xElement.GetDouble();
            y = yElement.GetDouble();
        }
        else
        {
            throw new InvalidDataException(
                $"Position #{index} of formation \"{formation}\" must be {{x, y}} or [x, y].");
        }

        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new InvalidDataException($"Position #{index} of formation \"{formation}\" is not finite.");

        return new ChorusFormationPoint { X = x, Y = y };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }
}
