using System.Text;

namespace Cohesive.Cli.Tests;

public sealed class CliStandardStreamsTests
{
    [Fact]
    public async Task OpenUtf8Writer_WritesWithoutBomAndLeavesOutputOpen()
    {
        await using MemoryStream output = new();

        await using (var writer = CliStandardStreams.OpenUtf8Writer(output))
        {
            await writer.WriteAsync("world-α");
        }

        Assert.True(output.CanWrite);
        Assert.Equal("world-α", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task OpenUtf8Reader_RejectsInvalidUtf8AndLeavesInputOpen()
    {
        await using MemoryStream input = new([0xc3, 0x28]);

        using (var reader = CliStandardStreams.OpenUtf8Reader(input))
        {
            await Assert.ThrowsAsync<DecoderFallbackException>(() => reader.ReadToEndAsync());
        }

        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task OpenUtf8Reader_ConsumesUtf8Bom()
    {
        byte[] inputBytes = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("world-α")];
        await using MemoryStream input = new(inputBytes);
        using var reader = CliStandardStreams.OpenUtf8Reader(input);

        var text = await reader.ReadToEndAsync();

        Assert.Equal("world-α", text);
    }
}
