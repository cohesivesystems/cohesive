namespace Cohesive.Model.Serialization;

// Native JSON interpretations must preserve the writer's replacement of malformed UTF-16.
internal static class JsonText
{
    internal static bool IsWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (char.IsSurrogate(text[i])
                && (!char.IsHighSurrogate(text[i]) || i + 1 == text.Length || !char.IsLowSurrogate(text[++i])))
                return false;
        return true;
    }
}
