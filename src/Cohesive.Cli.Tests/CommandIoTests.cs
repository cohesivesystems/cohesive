using System.Text;
using System.Text.Json;

namespace Cohesive.Cli.Tests;

public sealed class CommandIoTests
{
    [Fact]
    public async Task Null_WithCaptureChannels_WritesStrictUtf8WithoutBomAndLeavesChannelsOpen()
    {
        await using MemoryStream input = new(Encoding.UTF8.GetBytes("fixture"));
        await using MemoryStream output = new();
        using StringWriter error = new();
        var io = CommandIo.Null(
            standardInput: input,
            standardOutput: output,
            standardError: error);

        io.WriteLine("world-α");
        io.WriteErrorLine("diagnostic");

        Assert.Same(input, io.StandardInput);
        Assert.Same(output, io.StandardOutput);
        Assert.Same(error, io.StandardError);
        Assert.True(output.CanWrite);
        Assert.Equal($"world-α{Environment.NewLine}", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal($"diagnostic{Environment.NewLine}", error.ToString());
    }

    [Fact]
    public async Task ReadUtf8TextAsync_RejectsInvalidStandardInputAndLeavesItOpen()
    {
        await using MemoryStream input = new([0xc3, 0x28]);
        var io = CommandIo.Null(standardInput: input);

        await Assert.ThrowsAsync<DecoderFallbackException>(() =>
            io.ReadUtf8TextAsync(CommandIo.StandardStreamPath));

        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task WriteJson_UsesConfiguredSerializationPolicy()
    {
        await using MemoryStream output = new();
        JsonSerializerOptions jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        var io = CommandIo.Null(
            standardOutput: output,
            jsonSerializerOptions: jsonOptions);

        io.WriteJson(new JsonFixture("configured"));

        Assert.Same(jsonOptions, io.JsonSerializerOptions);
        Assert.Equal(
            $"{{\"sample_value\":\"configured\"}}{Environment.NewLine}",
            Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task ReadUtf8TextAsync_ConsumesUtf8BomFromStandardInput()
    {
        byte[] inputBytes = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("world-α")];
        await using MemoryStream input = new(inputBytes);
        var io = CommandIo.Null(standardInput: input);

        var text = await io.ReadUtf8TextAsync(CommandIo.StandardStreamPath);

        Assert.Equal("world-α", text);
    }

    [Fact]
    public async Task ReadInputAsync_SelectsFileOrStandardInput()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory, "input.txt");
            await File.WriteAllTextAsync(inputPath, "file");
            await using MemoryStream standardInput = new(Encoding.UTF8.GetBytes("standard"));
            var io = CommandIo.Null(standardInput: standardInput);

            var fromFile = await io.ReadUtf8TextAsync(inputPath);
            var fromStandardInput = await io.ReadUtf8TextAsync(CommandIo.StandardStreamPath);

            Assert.Equal("file", fromFile);
            Assert.Equal("standard", fromStandardInput);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteOutputAsync_ReplacesFileOnlyAfterSuccessfulWrite()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var outputPath = Path.Combine(temporaryDirectory, "output.txt");
            await File.WriteAllTextAsync(outputPath, "previous");
            var io = CommandIo.Null();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                io.WriteOutputAsync(
                    outputPath,
                    async (output, cancellationToken) =>
                    {
                        await output.WriteAsync(Encoding.UTF8.GetBytes("incomplete"), cancellationToken);
                        throw new InvalidOperationException("failed");
                    }));
            Assert.Equal("previous", await File.ReadAllTextAsync(outputPath));

            await io.WriteOutputAsync(
                outputPath,
                (output, cancellationToken) =>
                    output.WriteAsync(Encoding.UTF8.GetBytes("complete"), cancellationToken).AsTask());

            Assert.Equal("complete", await File.ReadAllTextAsync(outputPath));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(temporaryDirectory),
                path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cohesive-command-io-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    sealed record JsonFixture(string SampleValue);
}
