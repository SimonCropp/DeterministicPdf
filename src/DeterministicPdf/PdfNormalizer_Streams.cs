namespace DeterministicPdf;

public static partial class PdfNormalizer
{
    // Normalizing is not a streaming operation: the passes scan and patch arbitrary offsets
    // (the cross-reference table at the tail records positions of objects at the head), so the
    // whole document has to be resident. The result is therefore always a fresh MemoryStream,
    // positioned at 0 and ready to read.

    /// <summary>
    /// Reads <paramref name="source"/> from its current position to the end and returns a normalized
    /// copy, as a new <see cref="MemoryStream"/> positioned at 0.
    /// </summary>
    /// <remarks>
    /// The current position is honored for every stream type, including <see cref="MemoryStream"/>.
    /// </remarks>
    public static MemoryStream Normalize(Stream source) =>
        // The buffer is built here, so it is owned here: patch it in place rather than copying again.
        new(NormalizeCore(ToBytes(source), new()));

    /// <inheritdoc cref="Normalize(Stream)"/>
    public static async Task<MemoryStream> NormalizeAsync(Stream source, Cancel cancel = default) =>
        new(NormalizeCore(await ToBytesAsync(source, cancel), new()));

    /// <inheritdoc cref="Normalize(Stream)"/>
    /// <param name="source">The document to normalize.</param>
    /// <param name="changes"><inheritdoc cref="Normalize(byte[], out IReadOnlyList{NormalizeChange})" path="/param[@name='changes']"/></param>
    public static MemoryStream Normalize(Stream source, out IReadOnlyList<NormalizeChange> changes)
    {
        var recorder = new ChangeRecorder();
        var result = new MemoryStream(NormalizeCore(ToBytes(source), recorder));
        changes = recorder.Changes;
        return result;
    }

    // No async counterpart to the overload above, deliberately. Only reading the stream is
    // asynchronous — normalizing is synchronous work over the resident buffer — so an async report
    // overload would have to return the report beside the stream in a tuple or a wrapper, for no
    // gain over reading the bytes and calling Normalize(byte[], out ...).

    // Both paths read from the current position to the end, so the result never depends on the
    // concrete stream type. MemoryStream.ToArray is deliberately not used as the fast path: it
    // returns the whole buffer regardless of Position, which would silently disagree with the
    // CopyTo fallback for a stream that has already been partly read.
    static byte[] ToBytes(Stream source)
    {
        if (TryCopyRemaining(source, out var bytes))
        {
            return bytes;
        }

        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    }

    static async Task<byte[]> ToBytesAsync(Stream source, Cancel cancel)
    {
        if (TryCopyRemaining(source, out var bytes))
        {
            return bytes;
        }

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancel);
        return buffer.ToArray();
    }

    // Copies the unread remainder of a MemoryStream directly out of its backing array, avoiding the
    // second copy the CopyTo fallback would make. Fails for a MemoryStream constructed to hide its
    // buffer, which then takes the fallback.
    static bool TryCopyRemaining(Stream source, [NotNullWhen(true)] out byte[]? bytes)
    {
        if (source is not MemoryStream memoryStream ||
            !memoryStream.TryGetBuffer(out var segment))
        {
            bytes = null;
            return false;
        }

        var position = (int) memoryStream.Position;
        var count = segment.Count - position;
        bytes = new byte[count];
        Array.Copy(segment.Array!, segment.Offset + position, bytes, 0, count);
        return true;
    }
}
