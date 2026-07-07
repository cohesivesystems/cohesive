using System.Text.Json.Nodes;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

static class JsonArrayOps
{
    public static JsonArray InsertAt(JsonNode? source, JsonNode? indexValue, JsonNode? item, string entityId)
    {
        if (source is not JsonArray sourceArray)
            throw new SemanticRuleViolationException($"Function 'insertAt' expects an array as first argument on entity '{entityId}'.");

        var index = AsInt32(indexValue, "insertAt index");
        if (index < 0 || index > sourceArray.Count)
        {
            throw new SemanticRuleViolationException(
                $"Function 'insertAt' received out-of-range index '{index}' on entity '{entityId}'.");
        }

        JsonArray result = [];
        for (var i = 0; i <= sourceArray.Count; i++)
        {
            if (i == index)
                result.Add(CloneNode(item));

            if (i < sourceArray.Count)
                result.Add(CloneNode(sourceArray[i]));
        }

        return result;
    }

    public static JsonArray Append(JsonNode? source, JsonNode? item, string entityId)
    {
        if (source is not JsonArray sourceArray)
            throw new SemanticRuleViolationException($"Function 'append' expects an array as first argument on entity '{entityId}'.");

        JsonArray result = [];
        foreach (var sourceItem in sourceArray)
            result.Add(CloneNode(sourceItem));

        result.Add(CloneNode(item));
        return result;
    }

    public static JsonArray InsertRangeAt(JsonNode? source, JsonNode? indexValue, JsonNode? items, string entityId)
    {
        if (source is not JsonArray sourceArray)
            throw new SemanticRuleViolationException($"Function 'insertRangeAt' expects an array as first argument on entity '{entityId}'.");

        if (items is not JsonArray itemsArray)
            throw new SemanticRuleViolationException($"Function 'insertRangeAt' expects an array as third argument on entity '{entityId}'.");

        var index = AsInt32(indexValue, "insertRangeAt index");
        if (index < 0 || index > sourceArray.Count)
            throw new SemanticRuleViolationException($"Function 'insertRangeAt' received out-of-range index '{index}' on entity '{entityId}'.");

        JsonArray result = [];
        for (var i = 0; i < index; i++)
            result.Add(CloneNode(sourceArray[i]));

        foreach (var item in itemsArray)
            result.Add(CloneNode(item));

        for (var i = index; i < sourceArray.Count; i++)
            result.Add(CloneNode(sourceArray[i]));

        return result;
    }

    public static JsonArray AppendRange(JsonNode? source, JsonNode? items, string entityId)
    {
        if (source is not JsonArray sourceArray)
            throw new SemanticRuleViolationException($"Function 'appendRange' expects an array as first argument on entity '{entityId}'.");

        if (items is not JsonArray itemsArray)
            throw new SemanticRuleViolationException($"Function 'appendRange' expects an array as second argument on entity '{entityId}'.");

        JsonArray result = [];
        foreach (var sourceItem in sourceArray)
            result.Add(CloneNode(sourceItem));

        foreach (var item in itemsArray)
            result.Add(CloneNode(item));

        return result;
    }

    static int AsInt32(JsonNode? value, string context)
    {
        if (!JsonTypeSemantics.TryGetInt32(value, out var result))
            throw new SemanticRuleViolationException($"Expression value for '{context}' is not an int32.");

        return result;
    }

    static JsonNode? CloneNode(JsonNode? value) => value?.DeepClone();
}
