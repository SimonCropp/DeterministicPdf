using System.IO.Compression;

// A producer embeds a subset of whichever copy of a font the machine has installed, so two machines
// with different versions of one font render the same document to different bytes of different
// lengths. Every document here is built twice, around two font programs standing in for those two
// versions, in each of the cross-reference shapes the strip has to repair.
public class StripEmbeddedFontsTests
{
    static byte[] fontA = FontProgram(600, 1);
    static byte[] fontB = FontProgram(900, 2);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CollapsesDifferingFontProgramsToTheSameOutput(bool xrefStream)
    {
        var a = Build(fontA, xrefStream);
        var b = Build(fontB, xrefStream);
        await Assert.That(a.Length).IsNotEqualTo(b.Length);

        var strippedA = PdfNormalizer.Normalize(a, stripEmbeddedFonts: true);
        var strippedB = PdfNormalizer.Normalize(b, stripEmbeddedFonts: true);

        await Assert.That(strippedA).IsEquivalentTo(strippedB);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RemovesTheProgramAndTheDescriptorEntry(bool xrefStream)
    {
        var stripped = PdfNormalizer.Normalize(Build(fontA, xrefStream), stripEmbeddedFonts: true);
        var text = Encoding.Latin1.GetString(stripped);

        await Assert.That(text).DoesNotContain("/FontFile2");
        // The font is still named, just no longer embedded.
        await Assert.That(text).Contains("/FontName /Arial");
        // Both lengths the font stream declared, each held in an object of its own.
        await Assert.That(text).Contains("7 0 obj\n0\nendobj");
        await Assert.That(text).Contains("8 0 obj\n0\nendobj");
        await Assert.That(text).Contains("stream\r\n\r\nendstream");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RepairsTheCrossReferenceOffsets(bool xrefStream)
    {
        var stripped = PdfNormalizer.Normalize(Build(fontA, xrefStream), stripEmbeddedFonts: true);

        // The font stream sits ahead of two more objects and the cross-reference section, so all of
        // them moved. Reading the offsets back by hand is the real check: pdfium rebuilds a broken
        // table silently, so the document loading proves nothing about them.
        var offsets = ReadOffsets(stripped, xrefStream);
        await Assert.That(offsets.Count).IsGreaterThanOrEqualTo(8);
        var text = Encoding.Latin1.GetString(stripped);
        for (var number = 1; number < offsets.Count; number++)
        {
            await Assert.That(text[offsets[number]..]).StartsWith($"{number} 0 obj");
        }

        using var reader = DocLib.Instance.GetDocReader(stripped, new(scalingFactor: 2));
        await Assert.That(reader.GetPageCount()).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IsIdempotent(bool xrefStream)
    {
        var once = PdfNormalizer.Normalize(Build(fontA, xrefStream), stripEmbeddedFonts: true);
        var twice = PdfNormalizer.Normalize(once, true, out var changes);

        await Assert.That(twice).IsEquivalentTo(once);
        await Assert.That(changes).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ZeroesALengthLeftInAnObjectNothingRefersTo(bool xrefStream)
    {
        // Aspose.PDF states the length of the font stream directly, and still writes it into an object
        // of its own that nothing refers to. It measures the font program, so it differs with it.
        var a = Build(fontA, xrefStream, directLength: true);
        var b = Build(fontB, xrefStream, directLength: true);

        var strippedA = PdfNormalizer.Normalize(a, stripEmbeddedFonts: true);
        var strippedB = PdfNormalizer.Normalize(b, stripEmbeddedFonts: true);

        await Assert.That(strippedA).IsEquivalentTo(strippedB);
        var text = Encoding.Latin1.GetString(strippedA);
        await Assert.That(text).Contains("/Length 0 >>");
        await Assert.That(text).Contains("8 0 obj\n0\nendobj");
    }

    [Test]
    public async Task ReportsEachFontFileItRemoved()
    {
        PdfNormalizer.Normalize(Build(fontA, false), true, out var changes);

        await Assert.That(changes.Single(_ => _.Name == "/FontFile2").Count).IsEqualTo(1);
    }

    [Test]
    public async Task LeavesFontsEmbeddedUnlessAsked()
    {
        var input = Build(fontA, true);

        await Assert.That(PdfNormalizer.Normalize(input)).IsEquivalentTo(PdfNormalizer.Normalize(input, false));
        await Assert.That(Encoding.Latin1.GetString(PdfNormalizer.Normalize(input))).Contains("/FontFile2 6 0 R");
    }

    [Test]
    public async Task LeavesAnIncrementallyUpdatedDocumentAlone()
    {
        // A second startxref is an earlier revision's cross-reference section, holding offsets this
        // does not repair, so the fonts stay rather than leave them pointing at the wrong bytes.
        var input = Build(fontA, false)
            .Concat(Encoding.Latin1.GetBytes("startxref\n0\n%%EOF\n"))
            .ToArray();

        var normalized = PdfNormalizer.Normalize(input, true, out var changes);

        await Assert.That(Encoding.Latin1.GetString(normalized)).Contains("/FontFile2 6 0 R");
        await Assert.That(changes.Any(_ => _.Name == "/FontFile2")).IsFalse();
    }

    // Deterministic bytes that do not compress to nothing, so the two programs differ in compressed
    // length as well as in content.
    static byte[] FontProgram(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // A one-page document whose only font embeds 'fontProgram', with the lengths of the font stream
    // held in objects of their own (as Aspose.PDF writes them) and two objects after it, so that
    // removing the program moves something the cross-reference section points at. With 'directLength'
    // the stream states its own length, and the object that would have held it is left unreferenced.
    static byte[] Build(byte[] fontProgram, bool xrefStream, bool directLength = false)
    {
        var compressed = Compress(fontProgram);
        var length = "8 0 R";
        if (directLength)
        {
            length = $"{compressed.Length}";
        }

        List<string> objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Arial /FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /Arial /Flags 32 /FontFile2 6 0 R /StemV 80 >>",
            $"<< /Length1 7 0 R /Filter /FlateDecode /Length {length} >>\nstream\r\n{Encoding.Latin1.GetString(compressed)}\r\nendstream",
            $"{fontProgram.Length}",
            $"{compressed.Length}"
        ];

        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var number = 1; number <= objects.Count; number++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{number} 0 obj\n{objects[number - 1]}\nendobj\n");
        }

        var xref = builder.Length;
        if (xrefStream)
        {
            // One byte of type, two of offset, one of generation; the stream lists itself last.
            offsets.Add(xref);
            var entries = new List<byte>
            {
                0,
                0,
                0,
                255
            };
            foreach (var offset in offsets)
            {
                entries.AddRange([1, (byte) (offset >> 8), (byte) offset, 0]);
            }

            var data = Compress(entries.ToArray());
            builder.Append(
                $"{offsets.Count} 0 obj\n<< /Type /XRef /Size {offsets.Count + 1} /W [1 2 1] /Root 1 0 R /Filter /FlateDecode /Length {data.Length} >>\nstream\r\n{Encoding.Latin1.GetString(data)}\r\nendstream\nendobj\n");
        }
        else
        {
            builder.Append($"xref\n0 {objects.Count + 1}\n");
            builder.Append("0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                builder.Append($"{offset:D10} 00000 n \n");
            }

            builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        }

        builder.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
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

    // The offset the cross-reference section records for each object, indexed by object number. A
    // stripped cross-reference stream has been rewritten uncompressed, so its entries read directly.
    static List<int> ReadOffsets(byte[] data, bool xrefStream)
    {
        var text = Encoding.Latin1.GetString(data);
        var startxref = text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length;
        var section = int.Parse(text[startxref..text.IndexOf('\n', startxref)]);
        var offsets = new List<int>();
        if (xrefStream)
        {
            var content = text.IndexOf("stream\r\n", section, StringComparison.Ordinal) + "stream\r\n".Length;
            var end = text.IndexOf("\r\nendstream", content, StringComparison.Ordinal);
            for (var entry = content; entry < end; entry += 4)
            {
                offsets.Add(data[entry + 1] << 8 | data[entry + 2]);
            }

            return offsets;
        }

        var lines = text[section..text.IndexOf("trailer", section, StringComparison.Ordinal)].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(2))
        {
            offsets.Add(int.Parse(line[..10]));
        }

        return offsets;
    }
}
