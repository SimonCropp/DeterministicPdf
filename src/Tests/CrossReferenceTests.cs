using System.IO.Compression;
using System.Text.RegularExpressions;

// Removing the producer shortens the document, so every byte position it records has to be repaired,
// in every shape a cross-reference section takes: a classic table, a cross-reference stream (plain,
// or with the PNG predictor iText and PDFBox write), a hybrid of the two, and any number of them
// chained by incremental updates. Each document here is two revisions, the second updating the
// information dictionary the way iText appends to the producer each time it modifies a document.
public class CrossReferenceTests
{
    public enum Shape
    {
        Table,
        Stream,
        PredictedStream,
        Hybrid
    }

    [Test]
    [Arguments(Shape.Table)]
    [Arguments(Shape.Stream)]
    [Arguments(Shape.PredictedStream)]
    [Arguments(Shape.Hybrid)]
    public async Task RemovesProducerAcrossVersions(Shape shape)
    {
        var before = Build(shape, "iText Core 9.9.0");
        var after = Build(shape, "iText Core 9.10.0");
        await Assert.That(after.Length).IsNotEqualTo(before.Length);

        var fromBefore = PdfNormalizer.Normalize(before);
        var fromAfter = PdfNormalizer.Normalize(after);
        await Assert.That(fromAfter).IsEquivalentTo(fromBefore);
        await Assert.That(Encoding.Latin1.GetString(fromAfter)).Contains("/Producer ()");
    }

    [Test]
    [Arguments(Shape.Table)]
    [Arguments(Shape.Stream)]
    [Arguments(Shape.PredictedStream)]
    [Arguments(Shape.Hybrid)]
    public async Task RepairsEveryRevision(Shape shape)
    {
        var normalized = PdfNormalizer.Normalize(Build(shape, "iText Core 9.10.0"));
        await AssertOffsetsHold(normalized);

        using var reader = DocLib.Instance.GetDocReader(normalized, new(scalingFactor: 2));
        await Assert.That(reader.GetPageCount()).IsEqualTo(1);
    }

    [Test]
    [Arguments(Shape.Table)]
    [Arguments(Shape.Stream)]
    [Arguments(Shape.PredictedStream)]
    [Arguments(Shape.Hybrid)]
    public async Task IsIdempotent(Shape shape)
    {
        var once = PdfNormalizer.Normalize(Build(shape, "iText Core 9.10.0"));
        var twice = PdfNormalizer.Normalize(once, out var changes);

        await Assert.That(twice).IsEquivalentTo(once);
        await Assert.That(changes).IsEmpty();
    }

    [Test]
    public async Task WidensAnOffsetFieldThatNoLongerFits()
    {
        // The first revision's cross-reference stream is compressed and the second's records two-byte
        // offsets. Restated uncompressed, the first grows by far more than removing the producers
        // saves, which carries the second revision's objects past 65535: the field has to widen.
        var padding = 0;
        byte[] data;
        while (true)
        {
            data = Build(Shape.PredictedStream, "A", fillers: 300, padding: padding);
            var update = Encoding.Latin1.GetString(data).LastIndexOf("4 0 obj", StringComparison.Ordinal);
            if (padding != 0)
            {
                break;
            }

            padding = 65300 - update;
        }

        var normalized = PdfNormalizer.Normalize(data);
        var text = Encoding.Latin1.GetString(normalized);
        await Assert.That(text).Contains("/W [1 3 1]");
        await AssertOffsetsHold(normalized);

        using var reader = DocLib.Instance.GetDocReader(normalized, new(scalingFactor: 2));
        await Assert.That(reader.GetPageCount()).IsEqualTo(1);
    }

    [Test]
    public async Task LeavesALinearizedDocumentToTheInPlacePasses()
    {
        // Its hint tables record offsets too, in a form the repair does not rewrite, so the producer is
        // only zeroed: the document keeps its length.
        var data = Build(Shape.Table, "iText Core 9.10.0", linearized: true);
        var normalized = PdfNormalizer.Normalize(data);

        await Assert.That(normalized.Length).IsEqualTo(data.Length);
        await Assert.That(Encoding.Latin1.GetString(normalized)).Contains("/Producer (00000 0000 000000)");
    }

    // Two revisions: the first a one-page document whose information dictionary names 'producer', the
    // second replacing that dictionary with one that appends to it, and adding an object after it so
    // removing the producer moves something the second section points at.
    static byte[] Build(Shape shape, string producer, int fillers = 0, int padding = 0, bool linearized = false)
    {
        List<(int number, string body)> first = [];
        if (linearized)
        {
            first.Add((9, "<< /Linearized 1 >>"));
        }

        first.AddRange(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>"),
            (4, $"<< /Producer ({producer}) >>")
        ]);

        for (var filler = 0; filler < fillers; filler++)
        {
            first.Add((10 + filler, "<< >>"));
        }

        if (padding > 0)
        {
            first.Add((8, $"({new string('x', padding)})"));
        }

        List<(int number, string body)> second =
        [
            (4, $"<< /Producer ({producer}; modified using {producer}) >>"),
            (5, "<< /Note (update) >>")
        ];

        var size = 10 + fillers + 2;
        var builder = new StringBuilder("%PDF-1.7\n");
        int? previous = null;
        foreach (var (objects, streamNumber) in new[] { (first, size - 2), (second, size - 1) })
        {
            var offsets = new List<(int number, int offset)>();
            foreach (var (number, body) in objects)
            {
                offsets.Add((number, builder.Length));
                builder.Append($"{number} 0 obj\n{body}\nendobj\n");
            }

            var keys = $"/Size {size} /Root 1 0 R /Info 4 0 R";
            if (previous != null)
            {
                keys += $" /Prev {previous}";
            }

            var free = previous == null;
            int section;
            switch (shape)
            {
                case Shape.Table:
                    section = builder.Length;
                    AppendTable(builder, offsets, free, keys);
                    break;
                case Shape.Hybrid:
                    // The stream lists the same objects as the table, as a hybrid file's does for readers
                    // that understand it.
                    var stream = builder.Length;
                    AppendStream(builder, [..offsets, (streamNumber, stream)], streamNumber, free, $"/Size {size}", false, 2);
                    section = builder.Length;
                    AppendTable(builder, offsets, free, $"{keys} /XRefStm {stream}");
                    break;
                default:
                    section = builder.Length;
                    AppendStream(builder, [..offsets, (streamNumber, section)], streamNumber, free, keys, shape == Shape.PredictedStream, 2);
                    break;
            }

            builder.Append($"startxref\n{section}\n%%EOF\n");
            previous = section;
        }

        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    static void AppendTable(StringBuilder builder, List<(int number, int offset)> offsets, bool free, string keys)
    {
        builder.Append("xref\n");
        if (free)
        {
            builder.Append("0 1\n0000000000 65535 f \n");
        }

        foreach (var (number, offset) in offsets.OrderBy(_ => _.number))
        {
            builder.Append($"{number} 1\n{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< {keys} >>\n");
    }

    // A compressed cross-reference stream with one byte of type, 'offsetWidth' of offset and one of
    // generation, listing each object as a subsection of its own.
    static void AppendStream(StringBuilder builder, List<(int number, int offset)> offsets, int number, bool free, string keys, bool predicted, int offsetWidth)
    {
        var rows = new List<byte[]>();
        var index = new StringBuilder();
        if (free)
        {
            rows.Add(Entry(0, 0, offsetWidth, 255));
            index.Append("0 1 ");
        }

        foreach (var (objectNumber, offset) in offsets.OrderBy(_ => _.number))
        {
            rows.Add(Entry(1, offset, offsetWidth, 0));
            index.Append($"{objectNumber} 1 ");
        }

        var columns = offsetWidth + 2;
        var raw = new List<byte>();
        var above = new byte[columns];
        foreach (var row in rows)
        {
            if (predicted)
            {
                // PNG Up: each byte as its difference from the byte above it.
                raw.Add(2);
                for (var column = 0; column < columns; column++)
                {
                    raw.Add((byte) (row[column] - above[column]));
                }

                above = row;
            }
            else
            {
                raw.AddRange(row);
            }
        }

        var data = Compress(raw.ToArray());
        var parameters = "";
        if (predicted)
        {
            parameters = $" /DecodeParms << /Columns {columns} /Predictor 12 >>";
        }

        builder.Append(
            $"{number} 0 obj\n<< /Type /XRef {keys} /Index [{index.ToString().TrimEnd()}] /W [1 {offsetWidth} 1] /Filter /FlateDecode{parameters} /Length {data.Length} >>\nstream\r\n{Encoding.Latin1.GetString(data)}\r\nendstream\nendobj\n");
    }

    static byte[] Entry(int type, int offset, int offsetWidth, int generation)
    {
        var entry = new byte[offsetWidth + 2];
        entry[0] = (byte) type;
        for (var index = offsetWidth; index >= 1; index--)
        {
            entry[index] = (byte) offset;
            offset >>= 8;
        }

        entry[offsetWidth + 1] = (byte) generation;
        return entry;
    }

    static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
        {
            zlib.Write(bytes);
        }

        return output.ToArray();
    }

    // Follows the chain from every startxref by hand and checks every pointer lands on a section and
    // every in-use entry on its object. pdfium rebuilds a broken table silently, so the document
    // loading proves nothing about them. A rewritten cross-reference stream is uncompressed, so its
    // entries read directly.
    static async Task AssertOffsetsHold(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        var pending = new Queue<int>(Regex.Matches(text, @"startxref\s+(\d+)").Select(_ => int.Parse(_.Groups[1].Value)));
        await Assert.That(pending.Count).IsEqualTo(2);

        var visited = new HashSet<int>();
        var entries = 0;
        while (pending.Count > 0)
        {
            var section = pending.Dequeue();
            if (!visited.Add(section))
            {
                continue;
            }

            string dictionary;
            if (text[section..].StartsWith("xref"))
            {
                var trailer = text.IndexOf("trailer", section, StringComparison.Ordinal);
                foreach (Match entry in Regex.Matches(text[section..trailer], @"(\d+) 1\n(\d{10}) 00000 n "))
                {
                    await Assert.That(text[int.Parse(entry.Groups[2].Value)..]).StartsWith($"{entry.Groups[1].Value} 0 obj");
                    entries++;
                }

                dictionary = text[trailer..text.IndexOf(">>", trailer, StringComparison.Ordinal)];
            }
            else
            {
                await Assert.That(Regex.IsMatch(text[section..], @"^\d+ 0 obj\n<< /Type /XRef")).IsTrue();
                var dictionaryEnd = text.IndexOf(">>\nstream\r\n", section, StringComparison.Ordinal);
                dictionary = text[section..dictionaryEnd];
                await Assert.That(dictionary).DoesNotContain("/Filter");

                var widths = Regex.Match(dictionary, @"/W \[(\d+) (\d+) (\d+)\]");
                var typeWidth = int.Parse(widths.Groups[1].Value);
                var offsetWidth = int.Parse(widths.Groups[2].Value);
                var entryWidth = typeWidth + offsetWidth + int.Parse(widths.Groups[3].Value);
                var numbers = Regex.Match(dictionary, @"/Index \[([\d ]+)\]").Groups[1].Value
                    .Split(' ')
                    .Select(int.Parse)
                    .Chunk(2)
                    .SelectMany(_ => Enumerable.Range(_[0], _[1]))
                    .ToList();
                var content = dictionaryEnd + ">>\nstream\r\n".Length;
                for (var index = 0; index < numbers.Count; index++)
                {
                    var entry = content + index * entryWidth;
                    if (data[entry] != 1)
                    {
                        continue;
                    }

                    var offset = 0;
                    for (var field = 0; field < offsetWidth; field++)
                    {
                        offset = offset << 8 | data[entry + typeWidth + field];
                    }

                    await Assert.That(text[offset..]).StartsWith($"{numbers[index]} 0 obj");
                    entries++;
                }
            }

            foreach (Match pointer in Regex.Matches(dictionary, @"/(?:Prev|XRefStm) (\d+)"))
            {
                pending.Enqueue(int.Parse(pointer.Groups[1].Value));
            }
        }

        await Assert.That(entries).IsGreaterThan(0);
    }
}
