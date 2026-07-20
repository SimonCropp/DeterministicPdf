namespace DeterministicPdf;

public static partial class PdfNormalizer
{
    // Normalizing is not a streaming operation: the passes scan and patch arbitrary offsets
    // (the cross-reference table at the tail records positions of objects at the head), so the
    // whole document has to be resident. The result is therefore always a fresh MemoryStream,
    // positioned at 0 and ready to read.

    /// <summary>
    /// Reads <paramref name="source"/> from its current position to the end and returns a normalized copy.
    /// </summary>
    public static MemoryStream Normalize(Stream source) =>
        // The buffer is built here, so it is owned here: patch it in place rather than copying again.
        new(NormalizeCore(ToBytes(source)));

    /// <inheritdoc cref="Normalize(Stream)"/>
    public static async Task<MemoryStream> NormalizeAsync(Stream source, Cancel cancel = default) =>
        new(NormalizeCore(await ToBytesAsync(source, cancel)));

    static byte[] ToBytes(Stream source)
    {
        if (source is MemoryStream memoryStream)
        {
            return memoryStream.ToArray();
        }

        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    }

    static async Task<byte[]> ToBytesAsync(Stream source, Cancel cancel)
    {
        if (source is MemoryStream memoryStream)
        {
            return memoryStream.ToArray();
        }

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancel);
        return buffer.ToArray();
    }
}
