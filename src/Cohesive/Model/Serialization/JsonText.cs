using System.Text;

namespace Cohesive.Model.Serialization;

// Native JSON interpretations must preserve the writer's replacement of malformed UTF-16.
internal static class JsonText
{
    internal static bool IsWellFormed(string text) => IsWellFormed(text.AsSpan());

    internal static bool IsWellFormed(ReadOnlySpan<char> text)
    {
        const int AsciiFastPathThreshold = 32;
        if (text.Length >= AsciiFastPathThreshold && Ascii.IsValid(text)) return true;
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;
            if (!char.IsHighSurrogate(text[index]) || index + 1 == text.Length || !char.IsLowSurrogate(text[++index]))
                return false;
        }
        return true;
    }
}
