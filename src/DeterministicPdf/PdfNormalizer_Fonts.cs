namespace DeterministicPdf;

public static partial class PdfNormalizer
{
    // An embedded font program found through the /FontFile, /FontFile2 or /FontFile3 entry of a font
    // descriptor: the entry itself (key through the 'R' of its reference) and the stream it points at.
    record struct FontFile(string Key, int EntryStart, int EntryEnd, int Number, int Generation);

    // Removes every embedded font program and the font descriptor entry that points at it.
    //
    // A producer embeds a subset of whichever copy of a font the machine has installed, so the same
    // document rendered on a machine with Arial 7.01 and on one with Arial 7.06 carries two different
    // font programs. They draw the same glyphs, but they are different bytes of different lengths, and
    // being compressed there is nothing inside them to zero. The only way to make the two renders agree
    // is to drop the program, which leaves each font as one the document names but does not embed.
    //
    // That is a lossy edit - a viewer substitutes a font of its own - so it is opt-in, and it is why it
    // runs last: every other pass sees the document as the producer wrote it.
    //
    // Every length in the font stream's dictionary is restated as 0, and since the stream usually sits
    // in the middle of the body, everything after it moves, and TryRewrite repairs every offset that
    // records where.
    //
    // Returns the input array untouched when no font is embedded, or when the document is not a shape
    // this can safely rewrite.
    static byte[] StripEmbeddedFonts(byte[] data, ChangeRecorder recorder)
    {
        var fonts = FindFontFiles(data);
        if (fonts.Count == 0)
        {
            return data;
        }

        var edits = new List<(int start, int end, byte[] replacement)>();
        var stripped = new HashSet<(int number, int generation)>();
        foreach (var font in fonts)
        {
            edits.Add((font.EntryStart, font.EntryEnd, []));

            // Two descriptors may share one font program.
            if (stripped.Add((font.Number, font.Generation)) &&
                !TryStripFontStream(data, font, edits))
            {
                return data;
            }
        }

        if (!TryRewrite(data, edits, out var rebuilt))
        {
            return data;
        }

        foreach (var font in fonts)
        {
            recorder.Record(font.Key);
        }

        return rebuilt;
    }

    // Finds every "/FontFile n g R", "/FontFile2 n g R" and "/FontFile3 n g R" outside stream data.
    static List<FontFile> FindFontFiles(byte[] data)
    {
        var fonts = new List<FontFile>();
        var streams = FindStreamDataRegions(data);
        var key = "/FontFile"u8;
        var position = 0;
        while (true)
        {
            var hit = IndexOf(data, key, position);
            if (hit < 0)
            {
                return fonts;
            }

            var cursor = hit + key.Length;
            position = cursor;
            if (cursor < data.Length && data[cursor] is (byte) '2' or (byte) '3')
            {
                cursor++;
            }

            var keyEnd = cursor;
            if (!TryReadReference(data, ref cursor, out var number, out var generation) ||
                IsInsideStream(streams, hit))
            {
                continue;
            }

            var name = new char[keyEnd - hit];
            for (var index = 0; index < name.Length; index++)
            {
                name[index] = (char) data[hit + index];
            }

            fonts.Add(new(new(name), hit, cursor, number, generation));
            position = cursor;
        }
    }

    static bool IsInsideStream(List<(int start, int end)> streams, int position)
    {
        foreach (var stream in streams)
        {
            if (Contains(stream, position))
            {
                return true;
            }
        }

        return false;
    }

    // Adds the edits that empty one font stream: its data is removed, and /Length, /Length1, /Length2
    // and /Length3 are each restated as 0, in the dictionary or in the object an indirect one names.
    static bool TryStripFontStream(byte[] data, FontFile font, List<(int start, int end, byte[] replacement)> edits)
    {
        if (!TryFindStream(data, font.Number, font.Generation, out var dictionary, out var content))
        {
            return false;
        }

        edits.Add((content.start, content.end, []));
        AddOrphanedLengths(data, content.end - content.start, edits);

        var key = "/Length"u8;
        var position = dictionary.start;
        while (true)
        {
            var hit = IndexOf(data, key, position);
            if (hit < 0 || hit >= dictionary.end)
            {
                return true;
            }

            position = hit + key.Length;
            if (position < dictionary.end && data[position] is (byte) '1' or (byte) '2' or (byte) '3')
            {
                position++;
            }

            if (!TryResolveInt(data, position, out var field, out _))
            {
                return false;
            }

            edits.Add((field.start, field.end, "0"u8.ToArray()));
        }
    }

    // Aspose.PDF writes the compressed length of a stream into an object of its own even when the
    // dictionary states the length directly, leaving an object that nothing refers to but that still
    // holds a number as machine-dependent as the font program it measured. Each object that is nothing
    // but that number, and that no reference names, is restated as 0 too. An object something does
    // refer to is never touched: it could be the length of another stream that happens to match.
    static void AddOrphanedLengths(byte[] data, int length, List<(int start, int end, byte[] replacement)> edits)
    {
        var digits = AsciiDigits(length);
        var position = 0;
        while (true)
        {
            var hit = IndexOf(data, digits, position);
            if (hit < 0)
            {
                return;
            }

            position = hit + digits.Length;
            if (!StartsWith(data, SkipWhitespace(data, position), "endobj"u8) ||
                !TryReadObjectHeaderBefore(data, hit, out var number, out var generation) ||
                IsReferenced(data, number, generation))
            {
                continue;
            }

            edits.Add((hit, position, "0"u8.ToArray()));
        }
    }

    // Reads the "number generation obj" that ends, whitespace aside, at 'position'.
    static bool TryReadObjectHeaderBefore(byte[] data, int position, out int number, out int generation)
    {
        number = 0;
        generation = 0;

        var cursor = SkipWhitespaceBefore(data, position);
        if (cursor == position ||
            cursor < 3 ||
            !StartsWith(data, cursor - 3, "obj"u8))
        {
            return false;
        }

        cursor = SkipWhitespaceBefore(data, cursor - 3);
        var generationStart = SkipDigitsBefore(data, cursor);
        var numberEnd = SkipWhitespaceBefore(data, generationStart);
        var numberStart = SkipDigitsBefore(data, numberEnd);
        if (generationStart == cursor ||
            numberEnd == generationStart ||
            numberStart == numberEnd ||
            numberStart != 0 && data[numberStart - 1] is not ((byte) '\r' or (byte) '\n'))
        {
            return false;
        }

        cursor = numberStart;
        TryReadInt(data, ref cursor, out number);
        cursor = generationStart;
        TryReadInt(data, ref cursor, out generation);
        return true;
    }

    static int SkipWhitespaceBefore(byte[] data, int position)
    {
        while (position > 0 && IsWhitespace(data[position - 1]))
        {
            position--;
        }

        return position;
    }

    static int SkipDigitsBefore(byte[] data, int position)
    {
        while (position > 0 && IsDigit(data[position - 1]))
        {
            position--;
        }

        return position;
    }

    // Whether "number generation R" appears anywhere in the document.
    static bool IsReferenced(byte[] data, int number, int generation)
    {
        var digits = AsciiDigits(number);
        var position = 0;
        while (true)
        {
            var hit = IndexOf(data, digits, position);
            if (hit < 0)
            {
                return false;
            }

            position = hit + digits.Length;
            if (hit > 0 && IsDigit(data[hit - 1]))
            {
                continue;
            }

            var cursor = hit;
            if (TryReadReference(data, ref cursor, out var foundNumber, out var foundGeneration) &&
                foundNumber == number &&
                foundGeneration == generation)
            {
                return true;
            }
        }
    }

    // Locates the stream object "number generation obj": its dictionary and its data. The data is
    // measured by the /Length the dictionary declares rather than by searching for "endstream", which
    // compressed data is free to spell by accident; the keyword only has to be where the length says.
    static bool TryFindStream(byte[] data, int number, int generation, out (int start, int end) dictionary, out (int start, int end) content)
    {
        dictionary = default;
        content = default;

        var body = FindObject(data, number, generation);
        if (body < 0)
        {
            return false;
        }

        return TryReadStreamAt(data, body, out dictionary, out content);
    }

    // Reads the stream whose object body starts at 'body', just past its "obj". An incrementally updated
    // document may hold several objects of one number, so a section reached through a pointer is read
    // where the pointer says rather than found by its number.
    static bool TryReadStreamAt(byte[] data, int body, out (int start, int end) dictionary, out (int start, int end) content)
    {
        dictionary = default;
        content = default;

        var start = SkipWhitespace(data, body);
        var end = FindDictionaryEnd(data, start);
        if (end < 0)
        {
            return false;
        }

        dictionary = (start, end);
        var keyword = SkipWhitespace(data, end);
        if (!StartsWith(data, keyword, "stream"u8))
        {
            return false;
        }

        if (!TryFindKey(data, dictionary, "/Length"u8, out var lengthValue) ||
            !TryResolveInt(data, lengthValue, out _, out var length))
        {
            return false;
        }

        var contentStart = SkipEol(data, keyword + "stream"u8.Length);
        var contentEnd = contentStart + length;
        if (contentEnd > data.Length ||
            !StartsWith(data, SkipWhitespace(data, contentEnd), "endstream"u8))
        {
            return false;
        }

        content = (contentStart, contentEnd);
        return true;
    }

    // Sorts the edits by position and drops an exact repeat (two font streams naming one indirect
    // length). Fails when two edits overlap, which no well-formed document produces.
    static bool TrySortEdits(List<(int start, int end, byte[] replacement)> edits)
    {
        edits.Sort((left, right) => left.start.CompareTo(right.start));
        for (var index = edits.Count - 1; index > 0; index--)
        {
            var previous = edits[index - 1];
            var current = edits[index];
            if (current.start == previous.start &&
                current.end == previous.end)
            {
                edits.RemoveAt(index);
                continue;
            }

            if (current.start < previous.end)
            {
                return false;
            }
        }

        return true;
    }

    // Finds "number generation obj" at the start of a line and returns the position just past it, or -1.
    static int FindObject(byte[] data, int number, int generation)
    {
        var header = AsciiBytes($"{number} {generation} obj");
        var search = 0;
        while (true)
        {
            var hit = IndexOf(data, header, search);
            if (hit < 0)
            {
                return -1;
            }

            search = hit + header.Length;
            if (hit == 0 || data[hit - 1] is (byte) '\r' or (byte) '\n')
            {
                return search;
            }
        }
    }

    // Returns the position just past the ">>" that closes the dictionary opening at 'start', stepping
    // over nested dictionaries and over strings (which may hold either delimiter), or -1.
    static int FindDictionaryEnd(byte[] data, int start)
    {
        if (!StartsWith(data, start, "<<"u8))
        {
            return -1;
        }

        var depth = 0;
        var position = start;
        while (position < data.Length)
        {
            if (StartsWith(data, position, "<<"u8))
            {
                depth++;
                position += 2;
                continue;
            }

            if (StartsWith(data, position, ">>"u8))
            {
                depth--;
                position += 2;
                if (depth == 0)
                {
                    return position;
                }

                continue;
            }

            if (data[position] == (byte) '(')
            {
                position = FindLiteralEnd(data, position + 1);
            }
            else if (data[position] == (byte) '<')
            {
                position = FindByte(data, position, (byte) '>');
            }

            position++;
        }

        return -1;
    }

    // Finds 'key' in a dictionary and returns where its value starts. A longer name that merely begins
    // with the key (/Length1 for /Length, /Type for /T) is not a match.
    static bool TryFindKey(byte[] data, (int start, int end) dictionary, ReadOnlySpan<byte> key, out int value) =>
        TryFindEntry(data, dictionary, key, out _, out value);

    // As TryFindKey, also reporting where the key itself starts, for an edit that removes the entry.
    static bool TryFindEntry(byte[] data, (int start, int end) dictionary, ReadOnlySpan<byte> key, out int keyStart, out int value)
    {
        keyStart = -1;
        value = -1;
        var position = dictionary.start;
        while (true)
        {
            var hit = IndexOf(data, key, position);
            if (hit < 0 || hit >= dictionary.end)
            {
                return false;
            }

            position = hit + key.Length;
            if (position < dictionary.end && IsNameCharacter(data[position]))
            {
                continue;
            }

            keyStart = hit;
            value = SkipWhitespace(data, position);
            return true;
        }
    }

    static bool IsNameCharacter(byte b) =>
        !IsWhitespace(b) &&
        b is not ((byte) '/' or (byte) '[' or (byte) ']' or (byte) '<' or (byte) '>' or (byte) '(' or (byte) ')' or (byte) '{' or (byte) '}' or (byte) '%');

    // Reads an integer value that is either direct ("n") or a reference to the object holding it
    // ("n g R"), and reports both the value and the span of its digits wherever they live.
    static bool TryResolveInt(byte[] data, int position, out (int start, int end) field, out int value)
    {
        field = default;
        position = SkipWhitespace(data, position);
        var start = position;
        if (!TryReadInt(data, ref position, out value))
        {
            return false;
        }

        var cursor = start;
        if (!TryReadReference(data, ref cursor, out var number, out var generation))
        {
            field = (start, position);
            return true;
        }

        if (!TryFindIndirectLength(data, number, generation, out field))
        {
            return false;
        }

        cursor = field.start;
        return TryReadInt(data, ref cursor, out value);
    }

    // Reads "n g R", leaving 'position' just past the 'R'. 'position' is untouched on failure.
    static bool TryReadReference(byte[] data, ref int position, out int number, out int generation)
    {
        generation = 0;
        var cursor = SkipWhitespace(data, position);
        if (!TryReadInt(data, ref cursor, out number))
        {
            return false;
        }

        var separator = cursor;
        cursor = SkipWhitespace(data, cursor);
        if (cursor == separator ||
            !TryReadInt(data, ref cursor, out generation))
        {
            return false;
        }

        cursor = SkipWhitespace(data, cursor);
        if (cursor >= data.Length || data[cursor] != (byte) 'R')
        {
            return false;
        }

        position = cursor + 1;
        return true;
    }
}
