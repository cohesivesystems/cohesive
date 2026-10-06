using System.Buffers;
using System.Text.Json;

namespace Cohesive.Model.Serialization;

// Scan UTF-8 names directly. Hashes select candidates; ValueTextEquals proves equality after
// unescaping. Success paths never decode property-name strings or construct diagnostic paths.
internal static class JsonDuplicatePropertyScanner
{
    static readonly Scratch?[] Idle = new Scratch[8];
    static readonly object Gate = new();
    static int idleCount;

    internal static bool Scan(JsonElement element, string path, out string location)
    {
        if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            location = string.Empty;
            return false;
        }
        Scratch scratch;
        lock (Gate)
        {
            if (idleCount == 0)
                scratch = new Scratch();
            else
            {
                scratch = Idle[--idleCount]!;
                Idle[idleCount] = null;
                scratch.Buffer.Reset();
                scratch.Writer.Reset(scratch.Buffer);
            }
        }
        try
        {
            element.WriteTo(scratch.Writer);
            scratch.Writer.Flush();
            return Scan(scratch.Buffer.WrittenSpan, path, out location);
        }
        finally
        {
            scratch.Buffer.Dispose();
            // Reset releases the writer's active payload memory before caching it.
            scratch.Writer.Reset(scratch.Buffer);
            lock (Gate)
            {
                if (idleCount < Idle.Length)
                    Idle[idleCount++] = scratch;
                else
                    scratch.Writer.Dispose();
            }
        }
    }

    internal static bool Scan(ReadOnlySpan<byte> json, string path, out string location)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = int.MaxValue });
        reader.Read();
        if (ScanValue(ref reader, json, out location))
        {
            location = path + location;
            return true;
        }
        return false;
    }

    static bool ScanValue(ref Utf8JsonReader reader, ReadOnlySpan<byte> json, out string location)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
            return ScanObject(ref reader, json, out location);
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (ScanValue(ref reader, json, out location))
                {
                    location = "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + location;
                    return true;
                }
                index++;
            }
        }
        location = string.Empty;
        return false;
    }

    static bool ScanObject(ref Utf8JsonReader reader, ReadOnlySpan<byte> json, out string location)
    {
        Span<Name> names = stackalloc Name[32];
        names.Clear();
        Span<byte> nameBuffer = stackalloc byte[256];
        Name[]? rentedNames = null;
        byte[]? rentedText = null;
        var count = 0;
        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var property = reader;
                scoped ReadOnlySpan<byte> text = reader.ValueSpan;
                if (reader.ValueIsEscaped)
                {
                    if (text.Length > nameBuffer.Length)
                    {
                        if (rentedText is not null)
                            ArrayPool<byte>.Shared.Return(rentedText, clearArray: true);
                        rentedText = ArrayPool<byte>.Shared.Rent(text.Length);
                        nameBuffer = rentedText;
                    }
                    text = nameBuffer[..reader.CopyString(nameBuffer)];
                }
                ulong hash = 14695981039346656037;
                foreach (var value in text)
                    hash = unchecked((hash ^ value) * 1099511628211);
                var slot = (int)(hash & (uint)(names.Length - 1));
                while (names[slot].Offset != 0)
                {
                    var previous = names[slot];
                    if (previous.Hash == hash)
                    {
                        var priorReader = new Utf8JsonReader(json[previous.Offset..]);
                        priorReader.Read();
                        if (priorReader.ValueTextEquals(text))
                        {
                            location = "/" + Escape(reader.GetString()!);
                            return true;
                        }
                    }
                    slot = (slot + 1) & (names.Length - 1);
                }
                names[slot] = new(hash, checked((int)reader.TokenStartIndex));
                if (++count * 2 == names.Length)
                {
                    var expanded = ArrayPool<Name>.Shared.Rent(names.Length * 2);
                    expanded.AsSpan().Clear();
                    foreach (var name in names)
                    {
                        if (name.Offset == 0) continue;
                        var target = (int)(name.Hash & (uint)(expanded.Length - 1));
                        while (expanded[target].Offset != 0)
                            target = (target + 1) & (expanded.Length - 1);
                        expanded[target] = name;
                    }
                    if (rentedNames is not null)
                        ArrayPool<Name>.Shared.Return(rentedNames, clearArray: true);
                    rentedNames = expanded;
                    names = expanded;
                }
                reader.Read();
                if (ScanValue(ref reader, json, out location))
                {
                    location = "/" + Escape(property.GetString()!) + location;
                    return true;
                }
            }
            location = string.Empty;
            return false;
        }
        finally
        {
            if (rentedNames is not null)
                ArrayPool<Name>.Shared.Return(rentedNames, clearArray: true);
            if (rentedText is not null)
                ArrayPool<byte>.Shared.Return(rentedText, clearArray: true);
        }
    }

    static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    readonly record struct Name(ulong Hash, int Offset);

    sealed class Scratch
    {
        internal PooledByteBufferWriter Buffer { get; } = new();
        internal Utf8JsonWriter Writer { get; }
        internal Scratch() => Writer = new(Buffer, new JsonWriterOptions { MaxDepth = int.MaxValue });
    }
}
