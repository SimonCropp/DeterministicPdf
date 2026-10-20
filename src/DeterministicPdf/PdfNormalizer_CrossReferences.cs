using System.IO.Compression;

namespace DeterministicPdf;

public static partial class PdfNormalizer
{
    // Everything in a document that records a byte position, found by following the cross-reference
    // chain from every startxref: the pointers that name a section (each startxref, /Prev and
    // /XRefStm), the in-use entries of every classic table, and every cross-reference stream.
    class CrossReferences
    {
        public List<(int start, int end, int target)> Pointers = [];
        public List<(int field, int offset)> TableEntries = [];
        public List<XrefStream> Streams = [];
    }

    // One cross-reference stream, as much of it as the repair needs to restate it uncompressed: its
    // decoded entries, the field widths that divide them, and the spans of the dictionary that change
    // when it is rewritten.
    class XrefStream
    {
        public List<(long type, long offset, long generation)> Entries = [];
        public int TypeWidth;
        public int OffsetWidth;
        public int GenerationWidth;
        public (int start, int end) Widths;
        public (int start, int end) Length;
        public (int start, int end) Content;

        // The /Filter and /DecodeParms entries, key through value, which an uncompressed stream drops.
        public List<(int start, int end)> Encoding = [];
    }

    // Applies 'edits' and repairs every byte position the document records, in each of the shapes a
    // cross-reference section takes: a classic table, a cross-reference stream (rewritten uncompressed,
    // since a recompressed one would depend on the deflate implementation of the runtime doing the
    // work), a hybrid of the two, and any number of them chained by incremental updates.
    //
    // The repair edits depend on where everything ends up, and where everything ends up depends on
    // them: a pointer restated from 998 to 1002 grows a digit and moves whatever follows it, and so does
    // a cross-reference stream that has to widen its offset field. So they are rebuilt until their
    // lengths stop changing, which takes two or three rounds.
    //
    // Returns false, leaving the caller to keep the document as it was, when the edits overlap, the
    // chain cannot be followed, or the document is linearized: its hint tables record offsets too, in
    // a form this does not rewrite.
    static bool TryRewrite(byte[] data, List<(int start, int end, byte[] replacement)> edits, [NotNullWhen(true)] out byte[]? rebuilt)
    {
        rebuilt = null;
        if (!TrySortEdits(edits) ||
            !TryReadCrossReferences(data, out var references))
        {
            return false;
        }

        List<(int start, int end, byte[] replacement)> repairs = [];
        List<(int start, int end, byte[] replacement)> all = [..edits];
        for (var round = 0; ; round++)
        {
            if (round == 16)
            {
                return false;
            }

            var next = BuildRepairs(references, all);
            var settled = next.Count == repairs.Count &&
                          next.Zip(repairs).All(_ => _.First.replacement.Length == _.Second.replacement.Length);
            repairs = next;
            all = [..edits, ..repairs];
            if (settled)
            {
                break;
            }
        }

        if (!TrySortEdits(all) ||
            all.Count != edits.Count + repairs.Count)
        {
            return false;
        }

        rebuilt = ApplyEdits(data, all);

        // A classic entry keeps its length, so it is written into the rebuilt buffer rather than being
        // an edit of its own. The field and the object it points at are both original positions, mapped
        // through the same shift.
        foreach (var (field, offset) in references.TableEntries)
        {
            WriteOffset(rebuilt, Shift(all, field), Shift(all, offset));
        }

        return true;
    }

    // The edits that restate every pointer and cross-reference stream for the shift 'all' describes.
    static List<(int start, int end, byte[] replacement)> BuildRepairs(CrossReferences references, List<(int start, int end, byte[] replacement)> all)
    {
        var repairs = new List<(int start, int end, byte[] replacement)>();
        foreach (var (start, end, target) in references.Pointers)
        {
            repairs.Add((start, end, AsciiDigits(Shift(all, target))));
        }

        foreach (var stream in references.Streams)
        {
            // Only an in-use entry (type 1) records a byte position. A free entry, and an object held in
            // an object stream (type 2), are copied as they are.
            var entries = new List<(long type, long offset, long generation)>(stream.Entries.Count);
            foreach (var entry in stream.Entries)
            {
                if (entry.type == 1)
                {
                    entries.Add(entry with { offset = Shift(all, (int) entry.offset) });
                }
                else
                {
                    entries.Add(entry);
                }
            }

            // Everything after an earlier section moves, possibly further than its producer sized the
            // offset field for, so the field is widened when it has to be.
            var offsetWidth = Math.Max(stream.OffsetWidth, entries.Where(_ => _.type == 1).Select(_ => ByteWidth(_.offset)).DefaultIfEmpty(0).Max());
            var entryWidth = stream.TypeWidth + offsetWidth + stream.GenerationWidth;
            var content = new byte[entries.Count * entryWidth];
            for (var index = 0; index < entries.Count; index++)
            {
                var (type, offset, generation) = entries[index];
                var position = index * entryWidth;
                WriteBigEndian(content, position, stream.TypeWidth, type);
                WriteBigEndian(content, position + stream.TypeWidth, offsetWidth, offset);
                WriteBigEndian(content, position + stream.TypeWidth + offsetWidth, stream.GenerationWidth, generation);
            }

            foreach (var (start, end) in stream.Encoding)
            {
                repairs.Add((start, end, []));
            }

            if (offsetWidth != stream.OffsetWidth)
            {
                repairs.Add((stream.Widths.start, stream.Widths.end, AsciiBytes($"[{stream.TypeWidth} {offsetWidth} {stream.GenerationWidth}]")));
            }

            repairs.Add((stream.Length.start, stream.Length.end, AsciiDigits(content.Length)));
            repairs.Add((stream.Content.start, stream.Content.end, content));
        }

        repairs.Sort((left, right) => left.start.CompareTo(right.start));
        return repairs;
    }

    static int ByteWidth(long value)
    {
        var width = 1;
        while (value > 0xFF)
        {
            value >>= 8;
            width++;
        }

        return width;
    }

    // Follows the chain from every startxref outside stream data, through each section's /Prev and a
    // hybrid trailer's /XRefStm. Every pointer has to land on a section this can read.
    static bool TryReadCrossReferences(byte[] data, [NotNullWhen(true)] out CrossReferences? references)
    {
        references = null;

        // The linearization dictionary is the first object of the file.
        var linearized = IndexOf(data, "/Linearized"u8, 0);
        if (linearized is >= 0 and < 1024)
        {
            return false;
        }

        var result = new CrossReferences();
        var streams = FindStreamDataRegions(data);
        var pending = new Queue<int>();
        var position = 0;
        while (true)
        {
            var keyword = IndexOf(data, "startxref"u8, position);
            if (keyword < 0)
            {
                break;
            }

            position = keyword + "startxref"u8.Length;
            if (IsInsideStream(streams, keyword))
            {
                continue;
            }

            if (!TryReadPointer(data, position, result, pending))
            {
                return false;
            }
        }

        if (pending.Count == 0)
        {
            return false;
        }

        var visited = new HashSet<int>();
        while (pending.Count > 0)
        {
            var section = pending.Dequeue();
            if (!visited.Add(section))
            {
                continue;
            }

            var read = StartsWith(data, section, "xref"u8)
                ? TryReadTable(data, section, result, pending)
                : TryReadXrefStreamSection(data, section, result, pending);
            if (!read)
            {
                return false;
            }
        }

        references = result;
        return true;
    }

    // Reads the integer at 'position' as a pointer to a section, and queues that section.
    static bool TryReadPointer(byte[] data, int position, CrossReferences references, Queue<int> pending)
    {
        var start = SkipWhitespace(data, position);
        var end = start;
        if (!TryReadInt(data, ref end, out var target) ||
            target <= 0 ||
            target >= data.Length)
        {
            return false;
        }

        references.Pointers.Add((start, end, target));
        pending.Enqueue(target);
        return true;
    }

    // A classic section: "xref", its subsections, and the trailer dictionary after them.
    static bool TryReadTable(byte[] data, int section, CrossReferences references, Queue<int> pending)
    {
        var position = SkipEol(data, section + "xref"u8.Length);
        while (true)
        {
            position = SkipWhitespace(data, position);
            if (StartsWith(data, position, "trailer"u8))
            {
                break;
            }

            // A subsection header is two integers ("first count") separated by a space.
            if (!TryReadInt(data, ref position, out _))
            {
                return false;
            }

            position = SkipWhitespace(data, position);
            if (!TryReadInt(data, ref position, out var count))
            {
                return false;
            }

            position = SkipEol(data, position);
            for (var index = 0; index < count; index++)
            {
                // A cross-reference entry is exactly 20 bytes: a 10-digit offset, a space, a 5-digit
                // generation, a space, the in-use/free type byte, and a two-byte end-of-line.
                if (position + 20 > data.Length)
                {
                    return false;
                }

                if (data[position + 17] == (byte) 'n')
                {
                    references.TableEntries.Add((position, ParseFixedInt(data, position, 10)));
                }

                position += 20;
            }
        }

        var start = SkipWhitespace(data, position + "trailer"u8.Length);
        var end = FindDictionaryEnd(data, start);
        if (end < 0)
        {
            return false;
        }

        return TryReadSectionPointers(data, (start, end), references, pending);
    }

    // The /Prev every section may carry, and the /XRefStm of a hybrid trailer.
    static bool TryReadSectionPointers(byte[] data, (int start, int end) dictionary, CrossReferences references, Queue<int> pending)
    {
        if (TryFindKey(data, dictionary, "/Prev"u8, out var previous) &&
            !TryReadPointer(data, previous, references, pending))
        {
            return false;
        }

        return !TryFindKey(data, dictionary, "/XRefStm"u8, out var stream) ||
               TryReadPointer(data, stream, references, pending);
    }

    // A cross-reference stream: "number generation obj" at 'section', a stream whose /Type is /XRef.
    // Its entries are decoded (FlateDecode, optionally with a PNG predictor, or no filter at all) so the
    // repair can restate them uncompressed.
    static bool TryReadXrefStreamSection(byte[] data, int section, CrossReferences references, Queue<int> pending)
    {
        var cursor = section;
        if (!TryReadInt(data, ref cursor, out _))
        {
            return false;
        }

        cursor = SkipWhitespace(data, cursor);
        if (!TryReadInt(data, ref cursor, out _))
        {
            return false;
        }

        cursor = SkipWhitespace(data, cursor);
        if (!StartsWith(data, cursor, "obj"u8) ||
            !TryReadStreamAt(data, cursor + "obj"u8.Length, out var dictionary, out var content))
        {
            return false;
        }

        if (!TryFindKey(data, dictionary, "/Type"u8, out var type) ||
            !StartsWith(data, type, "/XRef"u8) ||
            !TryFindKey(data, dictionary, "/W"u8, out var widths) ||
            !TryReadWidths(data, widths, out var typeWidth, out var offsetWidth, out var generationWidth, out var widthsEnd) ||
            !TryFindKey(data, dictionary, "/Length"u8, out var lengthValue) ||
            !TryResolveInt(data, lengthValue, out var lengthField, out _))
        {
            return false;
        }

        var stream = new XrefStream
        {
            TypeWidth = typeWidth,
            OffsetWidth = offsetWidth,
            GenerationWidth = generationWidth,
            Widths = (widths, widthsEnd),
            Length = lengthField,
            Content = content
        };

        var decoded = new byte[content.end - content.start];
        Array.Copy(data, content.start, decoded, 0, decoded.Length);
        if (TryFindEntry(data, dictionary, "/Filter"u8, out var filterKey, out var filter))
        {
            if (!TryReadFlateFilter(data, filter, out var filterEnd) ||
                !TryInflate(data, content.start, content.end - content.start, out decoded))
            {
                return false;
            }

            stream.Encoding.Add((filterKey, filterEnd));
        }

        var entryWidth = typeWidth + offsetWidth + generationWidth;
        if (TryFindEntry(data, dictionary, "/DecodeParms"u8, out var parametersKey, out var parameters))
        {
            var parametersEnd = FindDictionaryEnd(data, parameters);
            if (parametersEnd < 0 ||
                !TryUnpredict(data, (parameters, parametersEnd), entryWidth, ref decoded))
            {
                return false;
            }

            stream.Encoding.Add((parametersKey, parametersEnd));
        }

        if (entryWidth == 0 ||
            typeWidth > 8 ||
            offsetWidth > 8 ||
            generationWidth > 8 ||
            decoded.Length % entryWidth != 0)
        {
            return false;
        }

        for (var entry = 0; entry < decoded.Length; entry += entryWidth)
        {
            // An absent type field means type 1.
            long entryType = 1;
            if (typeWidth != 0)
            {
                entryType = ReadBigEndian(decoded, entry, typeWidth);
            }

            var offset = ReadBigEndian(decoded, entry + typeWidth, offsetWidth);
            if (entryType == 1 &&
                offset > int.MaxValue)
            {
                return false;
            }

            stream.Entries.Add((entryType, offset, ReadBigEndian(decoded, entry + typeWidth + offsetWidth, generationWidth)));
        }

        references.Streams.Add(stream);
        return TryReadSectionPointers(data, dictionary, references, pending);
    }

    // Reverses the PNG predictor of a cross-reference stream: every row is a filter byte and then one
    // entry, each byte predicted from the byte before it in the row, the byte above it, or both. These
    // are the only predictors a producer writes a cross-reference stream with (iText and PDFBox use
    // Up, 12), so anything else - TIFF, or more than one byte per sample - is declined.
    static bool TryUnpredict(byte[] data, (int start, int end) parameters, int entryWidth, ref byte[] decoded)
    {
        if (!TryReadParameter(data, parameters, "/Predictor"u8, 1, out var predictor) ||
            !TryReadParameter(data, parameters, "/Columns"u8, 1, out var columns) ||
            !TryReadParameter(data, parameters, "/Colors"u8, 1, out var colors) ||
            !TryReadParameter(data, parameters, "/BitsPerComponent"u8, 8, out var bits))
        {
            return false;
        }

        if (predictor == 1)
        {
            return true;
        }

        if (predictor is < 10 or > 15 ||
            colors != 1 ||
            bits != 8 ||
            columns != entryWidth ||
            decoded.Length % (columns + 1) != 0)
        {
            return false;
        }

        var rows = decoded.Length / (columns + 1);
        var output = new byte[rows * columns];
        for (var row = 0; row < rows; row++)
        {
            var filter = decoded[row * (columns + 1)];
            var input = row * (columns + 1) + 1;
            var current = row * columns;
            var above = current - columns;
            for (var column = 0; column < columns; column++)
            {
                var left = 0;
                if (column > 0)
                {
                    left = output[current + column - 1];
                }

                var up = 0;
                var upLeft = 0;
                if (row > 0)
                {
                    up = output[above + column];
                    if (column > 0)
                    {
                        upLeft = output[above + column - 1];
                    }
                }

                int prediction;
                switch (filter)
                {
                    case 0:
                        prediction = 0;
                        break;
                    case 1:
                        prediction = left;
                        break;
                    case 2:
                        prediction = up;
                        break;
                    case 3:
                        prediction = (left + up) / 2;
                        break;
                    case 4:
                        prediction = Paeth(left, up, upLeft);
                        break;
                    default:
                        return false;
                }

                output[current + column] = (byte) (decoded[input + column] + prediction);
            }
        }

        decoded = output;
        return true;
    }

    static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var toLeft = Math.Abs(estimate - left);
        var toUp = Math.Abs(estimate - up);
        var toUpLeft = Math.Abs(estimate - upLeft);
        if (toLeft <= toUp &&
            toLeft <= toUpLeft)
        {
            return left;
        }

        if (toUp <= toUpLeft)
        {
            return up;
        }

        return upLeft;
    }

    // Reads a direct integer from the decode parameters, or 'fallback' when the key is absent.
    static bool TryReadParameter(byte[] data, (int start, int end) parameters, ReadOnlySpan<byte> key, int fallback, out int value)
    {
        value = fallback;
        if (!TryFindKey(data, parameters, key, out var position))
        {
            return true;
        }

        return TryReadInt(data, ref position, out value);
    }

    static long ReadBigEndian(byte[] data, int position, int width)
    {
        long value = 0;
        for (var index = 0; index < width; index++)
        {
            value = value << 8 | data[position + index];
        }

        return value;
    }

    static void WriteBigEndian(byte[] data, int position, int width, long value)
    {
        for (var index = width - 1; index >= 0; index--)
        {
            data[position + index] = (byte) value;
            value >>= 8;
        }
    }

    // "[type offset generation]", reporting where the array ends.
    static bool TryReadWidths(byte[] data, int position, out int type, out int offset, out int generation, out int end)
    {
        type = 0;
        offset = 0;
        generation = 0;
        end = -1;
        if (position >= data.Length || data[position] != (byte) '[')
        {
            return false;
        }

        position = SkipWhitespace(data, position + 1);
        if (!TryReadInt(data, ref position, out type))
        {
            return false;
        }

        position = SkipWhitespace(data, position);
        if (!TryReadInt(data, ref position, out offset))
        {
            return false;
        }

        position = SkipWhitespace(data, position);
        if (!TryReadInt(data, ref position, out generation))
        {
            return false;
        }

        position = SkipWhitespace(data, position);
        if (position >= data.Length || data[position] != (byte) ']')
        {
            return false;
        }

        end = position + 1;
        return true;
    }

    // Accepts "/FlateDecode" and "[/FlateDecode]", the only filter a cross-reference stream is decoded
    // from here, and reports where the value ends.
    static bool TryReadFlateFilter(byte[] data, int position, out int end)
    {
        end = -1;
        var array = position < data.Length && data[position] == (byte) '[';
        if (array)
        {
            position = SkipWhitespace(data, position + 1);
        }

        if (!StartsWith(data, position, "/FlateDecode"u8))
        {
            return false;
        }

        position += "/FlateDecode"u8.Length;
        if (!array)
        {
            end = position;
            return true;
        }

        position = SkipWhitespace(data, position);
        if (position >= data.Length || data[position] != (byte) ']')
        {
            return false;
        }

        end = position + 1;
        return true;
    }

    // Decodes zlib data: a two byte header, a deflate body, and a checksum the decoder never reads.
    static bool TryInflate(byte[] data, int start, int length, out byte[] decoded)
    {
        decoded = [];
        if (length < 2)
        {
            return false;
        }

        try
        {
            using var input = new MemoryStream(data, start + 2, length - 2);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            decoded = output.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
