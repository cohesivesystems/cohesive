using System.Text;

namespace Cohesive.Cli;

/// <summary>Creates text adapters for caller-owned CLI standard streams.</summary>
/// <remarks>
/// The adapters use strict UTF-8 without emitting a byte-order mark. Input recognizes byte-order marks so redirected
/// text produced by standard platform writers remains consumable. Disposing an adapter flushes its buffers but leaves
/// the underlying stream open.
/// </remarks>
public static class CliStandardStreams
{
    /// <summary>Conventional path token used to select a standard input or output stream.</summary>
    public const string StandardStreamPath = "-";

    static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>Determines whether a path selects a standard process stream.</summary>
    /// <param name="path">Path value to inspect.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> is <see cref="StandardStreamPath"/>.</returns>
    public static bool IsStandardStreamPath(string? path) => string.Equals(
        path,
        StandardStreamPath,
        StringComparison.Ordinal);

    /// <summary>Opens a BOM-aware strict UTF-8 reader over a caller-owned standard input stream.</summary>
    /// <param name="input">Readable stream containing command input text.</param>
    /// <returns>A reader that leaves <paramref name="input"/> open when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> is not readable.</exception>
    public static StreamReader OpenUtf8Reader(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new(
            input,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: -1,
            leaveOpen: true);
    }

    /// <summary>Opens a strict UTF-8 writer without a byte-order mark over a caller-owned standard output stream.</summary>
    /// <param name="output">Writable stream receiving command output text.</param>
    /// <returns>A writer that flushes buffered text and leaves <paramref name="output"/> open when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="output"/> is not writable.</exception>
    public static StreamWriter OpenUtf8Writer(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new(
            output,
            StrictUtf8,
            bufferSize: -1,
            leaveOpen: true);
    }
}
