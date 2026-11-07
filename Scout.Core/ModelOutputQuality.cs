using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Scout.Core;

/// <summary>Rejects corrupt generated prose; it does not determine whether a match is true.</summary>
public static class ModelOutputQuality
{
    public static void ValidateSavedResult(JsonObject result)
    {
        ValidateExplanations(result);
        if (result["verification"]?["initial_result"] is JsonObject initial) ValidateExplanations(initial);
        if (result["conflict_verification"]?["independent_result"] is JsonObject independent) ValidateExplanations(independent);
        // Rejected attempts are diagnostic history, not accepted model results.
    }

    public static void ValidateExplanations(JsonObject result)
    {
        ValidateText(result["summary"], "summary", 650);
        Fields("candidate_checks", "event_comparison", 450, 5);
        Fields("matches", "reason", 450, 2);
        Fields("alternatives", "reason_against", 300, 2);

        void Fields(string array, string field, int maxLength, int maxItems)
        {
            if (result[array] is not JsonArray items || items.Count > maxItems)
                throw new InvalidDataException($"Invalid {array} structure.");
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not JsonObject item)
                    throw new InvalidDataException($"Invalid {array}[{i}] structure.");
                ValidateText(item[field], $"{array}[{i}].{field}", maxLength);
            }
        }
    }

    public static void ValidateText(JsonNode? value, string field, int maxLength)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException($"Missing generated explanation in {field}.");
        // JSON Schema counts Unicode characters, not UTF-16 code units.
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length > maxLength)
            throw new InvalidDataException($"Generated explanation in {field} exceeds {maxLength} characters.");
        int repeated = 0;
        Rune previous = default;
        foreach (var rune in runes)
        {
            repeated = rune == previous && !Rune.IsWhiteSpace(rune) ? repeated + 1 : 1;
            previous = rune;
            if (repeated >= 32) Reject();
        }
        var words = Regex.Matches(text, @"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*")
            .Select(m => m.Value).ToArray();
        // Conservative loops: twelve repeated words, or a repeated 2-8 word phrase
        // appearing at least four times and spanning at least twelve words.
        for (int width = 1; width <= 8; width++)
        {
            int equal = 0;
            int repetitions = width == 1 ? 12 : Math.Max(4, (12 + width - 1) / width);
            for (int i = width; i < words.Length; i++)
            {
                equal = string.Equals(words[i], words[i - width], StringComparison.OrdinalIgnoreCase) ? equal + 1 : 0;
                if (equal >= width * (repetitions - 1)) Reject();
            }
        }
        // Never echo corrupt model content into retry prompts or diagnostics.
        void Reject() => throw new InvalidDataException($"Repetitive generated explanation in {field}; no assignment accepted. Write a concise factual comparison without loops or filler.");
    }
}
