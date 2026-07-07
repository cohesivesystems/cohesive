using System.Text;

namespace Cohesive.CodeGen.Cli;

/// <summary>
/// Writes generated artifacts without changing timestamps when content is unchanged.
/// </summary>
public static class GeneratedArtifactWriter
{
    static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes the supplied text only when the target file content has changed.
    /// </summary>
    public static bool WriteIfChanged(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            if (string.Equals(existing, text, StringComparison.Ordinal))
                return false;
        }

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, text, Utf8WithoutBom);
        File.Move(tempPath, path, overwrite: true);
        return true;
    }
}
