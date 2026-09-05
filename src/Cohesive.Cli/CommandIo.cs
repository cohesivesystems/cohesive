using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Cli;

/// <summary>
/// Owns the input, output, error, encoding, and JSON policy for one CLI invocation environment.
/// </summary>
/// <remarks>
/// Supplied streams and writers remain caller-owned. Text output uses strict UTF-8 without a byte-order mark.
/// File output is written to a same-directory temporary file and atomically replaces the destination only after
/// the write completes successfully. Standard output is streamed directly and therefore cannot provide the same
/// rollback guarantee.
/// </remarks>
public sealed class CommandIo
{
    /// <summary>Conventional path token used to select standard input or standard output.</summary>
    public const string StandardStreamPath = "-";

    static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    readonly TextWriter textOutput;

    /// <summary>Initializes an invocation I/O environment.</summary>
    /// <param name="standardInput">Readable raw standard-input stream.</param>
    /// <param name="standardOutput">Writable raw standard-output stream.</param>
    /// <param name="standardError">Text writer receiving diagnostics and errors.</param>
    /// <param name="jsonSerializerOptions">
    /// Optional JSON policy; web defaults with indented camel-case properties and string enums are used when omitted.
    /// </param>
    /// <exception cref="ArgumentNullException">Any required channel is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="standardInput"/> is not readable, or <paramref name="standardOutput"/> is not writable.
    /// </exception>
    public CommandIo(
        Stream standardInput,
        Stream standardOutput,
        TextWriter standardError,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        if (!standardInput.CanRead)
        {
            throw new ArgumentException("The standard input stream must be readable.", nameof(standardInput));
        }

        if (!standardOutput.CanWrite)
        {
            throw new ArgumentException("The standard output stream must be writable.", nameof(standardOutput));
        }

        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
        JsonSerializerOptions = jsonSerializerOptions ?? CreateDefaultJsonSerializerOptions();
        textOutput = new StreamWriter(
            standardOutput,
            StrictUtf8,
            bufferSize: -1,
            leaveOpen: true)
        {
            AutoFlush = true
        };
    }

    /// <summary>Gets the caller-owned raw standard-input stream.</summary>
    public Stream StandardInput { get; }

    /// <summary>Gets the caller-owned raw standard-output stream.</summary>
    public Stream StandardOutput { get; }

    /// <summary>Gets the caller-owned standard-error writer.</summary>
    public TextWriter StandardError { get; }

    /// <summary>Gets the JSON serialization policy used by JSON output helpers.</summary>
    public JsonSerializerOptions JsonSerializerOptions { get; }

    internal TextWriter TextOutput => textOutput;

    /// <summary>Creates an I/O environment backed by process console channels.</summary>
    /// <param name="standardInput">Optional readable override for process standard input.</param>
    /// <param name="standardOutput">Optional writable override for process standard output.</param>
    /// <param name="standardError">Optional writer override for process standard error.</param>
    /// <param name="jsonSerializerOptions">Optional JSON serialization policy.</param>
    /// <returns>A console-based I/O environment whose channels remain caller-owned.</returns>
    /// <exception cref="ArgumentException">A supplied input or output stream has incompatible access.</exception>
    public static CommandIo Console(
        Stream? standardInput = null,
        Stream? standardOutput = null,
        TextWriter? standardError = null,
        JsonSerializerOptions? jsonSerializerOptions = null) =>
        new(
            standardInput ?? System.Console.OpenStandardInput(),
            standardOutput ?? System.Console.OpenStandardOutput(),
            standardError ?? System.Console.Error,
            jsonSerializerOptions);

    /// <summary>Creates a silent I/O environment with optional caller-supplied capture channels.</summary>
    /// <param name="standardInput">Optional readable input; <see cref="Stream.Null"/> is used when omitted.</param>
    /// <param name="standardOutput">Optional writable output capture; <see cref="Stream.Null"/> is used when omitted.</param>
    /// <param name="standardError">Optional error capture; <see cref="TextWriter.Null"/> is used when omitted.</param>
    /// <param name="jsonSerializerOptions">Optional JSON serialization policy.</param>
    /// <returns>A null-based I/O environment whose supplied channels remain caller-owned.</returns>
    /// <exception cref="ArgumentException">A supplied input or output stream has incompatible access.</exception>
    public static CommandIo Null(
        Stream? standardInput = null,
        Stream? standardOutput = null,
        TextWriter? standardError = null,
        JsonSerializerOptions? jsonSerializerOptions = null) =>
        new(
            standardInput ?? Stream.Null,
            standardOutput ?? Stream.Null,
            standardError ?? TextWriter.Null,
            jsonSerializerOptions);

    /// <summary>Determines whether a path selects a standard stream.</summary>
    /// <param name="path">Path value to inspect.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> is <see cref="StandardStreamPath"/>.</returns>
    public static bool IsStandardStreamPath(string? path) => string.Equals(
        path,
        StandardStreamPath,
        StringComparison.Ordinal);

    /// <summary>Writes one line to standard output.</summary>
    /// <param name="value">Line content, or <see langword="null"/> for an empty line.</param>
    public void WriteLine(string? value) => textOutput.WriteLine(value);

    /// <summary>Writes one line to standard error.</summary>
    /// <param name="value">Line content, or <see langword="null"/> for an empty line.</param>
    public void WriteErrorLine(string? value) => StandardError.WriteLine(value);

    /// <summary>Serializes a value as JSON and writes it to standard output.</summary>
    /// <param name="value">Value to serialize.</param>
    /// <param name="jsonSerializerOptions">Optional invocation-local override of the configured JSON policy.</param>
    /// <exception cref="NotSupportedException">No compatible JSON converter exists for <paramref name="value"/>.</exception>
    public void WriteJson(object value, JsonSerializerOptions? jsonSerializerOptions = null) =>
        WriteLine(JsonSerializer.Serialize(value, jsonSerializerOptions ?? JsonSerializerOptions));

    /// <summary>Serializes a value as JSON and writes it to standard error.</summary>
    /// <param name="value">Value to serialize.</param>
    /// <exception cref="NotSupportedException">No compatible JSON converter exists for <paramref name="value"/>.</exception>
    public void WriteJsonError(object value) =>
        WriteErrorLine(JsonSerializer.Serialize(value, JsonSerializerOptions));

    /// <summary>Reads input selected by a file path or <see cref="StandardStreamPath"/>.</summary>
    /// <typeparam name="TResult">Result produced by the input reader.</typeparam>
    /// <param name="path">Input file path, or <see cref="StandardStreamPath"/> for standard input.</param>
    /// <param name="readAsync">Operation that consumes the selected readable stream.</param>
    /// <param name="cancellationToken">Cancellation token for opening and reading input.</param>
    /// <returns>The result produced by <paramref name="readAsync"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="readAsync"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The selected file cannot be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the selected file is denied.</exception>
    /// <remarks>
    /// The selected stream is borrowed for the duration of <paramref name="readAsync"/>. File streams are disposed
    /// after the operation completes; standard input remains open.
    /// </remarks>
    public async Task<TResult> ReadInputAsync<TResult>(
        string path,
        Func<Stream, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(readAsync);
        if (IsStandardStreamPath(path))
        {
            return await readAsync(StandardInput, cancellationToken).ConfigureAwait(false);
        }

        await using FileStream input = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await readAsync(input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads strict UTF-8 text selected by a file path or <see cref="StandardStreamPath"/>.</summary>
    /// <param name="path">Input file path, or <see cref="StandardStreamPath"/> for standard input.</param>
    /// <param name="cancellationToken">Cancellation token for opening and reading input.</param>
    /// <returns>The complete decoded input text.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.</exception>
    /// <exception cref="IOException">The selected file cannot be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the selected file is denied.</exception>
    /// <exception cref="DecoderFallbackException">The input is not valid UTF-8.</exception>
    public Task<string> ReadUtf8TextAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        ReadInputAsync(
            path,
            static async (input, token) =>
            {
                using var reader = OpenUtf8Reader(input);
                return await reader.ReadToEndAsync(token).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>Writes output selected by a file path or <see cref="StandardStreamPath"/>.</summary>
    /// <param name="path">Output file path, or <see cref="StandardStreamPath"/> for standard output.</param>
    /// <param name="writeAsync">Operation that writes to the selected stream.</param>
    /// <param name="cancellationToken">Cancellation token for writing output.</param>
    /// <returns>A task that completes after standard output is flushed or the destination file is atomically replaced.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="writeAsync"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The destination cannot be created, written, or replaced.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the destination is denied.</exception>
    /// <remarks>
    /// The selected stream is borrowed for the duration of <paramref name="writeAsync"/> and must not be disposed by
    /// the operation. A failed file operation preserves an existing destination whenever the platform's atomic move
    /// guarantee is available; a failed standard-output operation may have already emitted bytes.
    /// </remarks>
    public async Task WriteOutputAsync(
        string path,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writeAsync);
        if (IsStandardStreamPath(path))
        {
            await writeAsync(StandardOutput, cancellationToken).ConfigureAwait(false);
            await StandardOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var destination = Path.GetFullPath(path);
        var outputDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Output path '{destination}' has no parent directory.");
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream temporaryOutput = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await writeAsync(temporaryOutput, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    internal static StreamReader OpenUtf8Reader(Stream input) => new(
        input,
        StrictUtf8,
        detectEncodingFromByteOrderMarks: true,
        bufferSize: -1,
        leaveOpen: true);

    static JsonSerializerOptions CreateDefaultJsonSerializerOptions() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the write failure. The uniquely named temporary artifact remains diagnosable.
        }
    }
}
