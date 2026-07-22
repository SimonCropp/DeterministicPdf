public class PdfNormalizerTests
{
    [Test]
    public async Task NeutralizesVolatileValues()
    {
        var input =
            "/ID [<A1B2C3D4E5F60718> <1122334455667788>] " +
            "/CreationDate(D:20240115093000+05'30') " +
            "/ModDate(D:20240115093000Z) " +
            "<xmp:CreateDate>2024-01-15T09:30:00+05:30</xmp:CreateDate>" +
            "<xmp:ModifyDate>2024-01-15T09:30:00Z</xmp:ModifyDate>" +
            "<xmp:MetadataDate>2024-01-15T09:30:00Z</xmp:MetadataDate>" +
            "<xmpMM:DocumentID>uuid:0f7b2c9a-1234-5678-9abc-def012345678</xmpMM:DocumentID>" +
            "<xmpMM:InstanceID>xmp.iid:1a2b3c4d</xmpMM:InstanceID>";
        var expected =
            "/ID [<0000000000000000> <0000000000000000>] " +
            "/CreationDate(D:00000000000000+00'00') " +
            "/ModDate(D:00000000000000Z) " +
            "<xmp:CreateDate>0000-00-00T00:00:00+00:00</xmp:CreateDate>" +
            "<xmp:ModifyDate>0000-00-00T00:00:00Z</xmp:ModifyDate>" +
            "<xmp:MetadataDate>0000-00-00T00:00:00Z</xmp:MetadataDate>" +
            $"<xmpMM:DocumentID>{new string('0', 41)}</xmpMM:DocumentID>" +
            $"<xmpMM:InstanceID>{new string('0', 16)}</xmpMM:InstanceID>";
        await Assert.That(Normalize(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task NeutralizesDublinCoreDate()
    {
        // Some producers (for example older Apache FOP) write the render time straight into the
        // Dublin Core <dc:date> element as simple text content.
        var input = "<dc:date>2024-01-15T09:30:00+05:30</dc:date>";
        var expected = "<dc:date>0000-00-00T00:00:00+00:00</dc:date>";
        await Assert.That(Normalize(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task NeutralizesDublinCoreDateSeq()
    {
        // Per the XMP spec dc:date is an ordered array (seq Date), so a spec-compliant producer
        // (current Apache FOP) nests the render time in rdf:Seq/rdf:li rather than as direct text.
        var input = "<dc:date><rdf:Seq><rdf:li>2024-01-15T09:30:00+05:30</rdf:li></rdf:Seq></dc:date>";
        var expected = "<dc:date><rdf:Seq><rdf:li>0000-00-00T00:00:00+00:00</rdf:li></rdf:Seq></dc:date>";
        await Assert.That(Normalize(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task NeutralizesDublinCoreDateSeqWithWhitespaceAndMultipleEntries()
    {
        // Pretty-printed with indentation and more than one date in the sequence: markup and
        // whitespace are preserved while every date value is zeroed.
        var input =
            """
            <dc:date>
              <rdf:Seq>
                <rdf:li>2024-01-15T09:30:00+05:30</rdf:li>
                <rdf:li>2019-12-31T23:59:59Z</rdf:li>
              </rdf:Seq>
            </dc:date>
            """;
        var expected =
            """
            <dc:date>
              <rdf:Seq>
                <rdf:li>0000-00-00T00:00:00+00:00</rdf:li>
                <rdf:li>0000-00-00T00:00:00Z</rdf:li>
              </rdf:Seq>
            </dc:date>
            """;
        await Assert.That(Normalize(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task LeavesNonDateRdfArraysUntouched()
    {
        // The rdf:li descent is scoped to dc:date, so digits in a sibling array (here dc:subject)
        // must survive.
        var input =
            "<dc:subject><rdf:Bag><rdf:li>topic 2024</rdf:li></rdf:Bag></dc:subject>" +
            "<dc:date><rdf:Seq><rdf:li>2024-01-15T09:30:00Z</rdf:li></rdf:Seq></dc:date>";
        var expected =
            "<dc:subject><rdf:Bag><rdf:li>topic 2024</rdf:li></rdf:Bag></dc:subject>" +
            "<dc:date><rdf:Seq><rdf:li>0000-00-00T00:00:00Z</rdf:li></rdf:Seq></dc:date>";
        await Assert.That(Normalize(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task CollapsesDifferingValuesToTheSameOutput()
    {
        // The same producer emits a stable structure across runs, so two documents differing only
        // in the volatile digits/hex normalize to identical bytes.
        var a = "/ID [<A1B2C3D4>] /CreationDate(D:20240115093000+05'30')";
        var b = "/ID [<99887766>] /CreationDate(D:19991231235959+11'45')";
        await Assert.That(a).IsNotEqualTo(b);
        await Assert.That(Normalize(a)).IsEqualTo(Normalize(b));
    }

    [Test]
    public async Task LeavesLookalikeKeysUntouched()
    {
        // /IDTree is a name-tree key (not the file identifier), /ModDateStamp is a different name,
        // and a self-closing date element has no content: none should be altered.
        var input = "/IDTree [1 2] /ModDateStamp(20240101) <xmp:CreateDate/>2024";
        await Assert.That(Normalize(input)).IsEqualTo(input);
    }

    [Test]
    public async Task HandlesUnterminatedFileIdWithoutOverrunning()
    {
        // A truncated /ID whose string runs to the end of the buffer with no closing '>'/')' (and no
        // closing ']') must not scan past the buffer: the value is zeroed as far as it exists and the
        // scan stops cleanly rather than throwing. Hex string form (no closing '>'):
        await Assert.That(Normalize("/ID [<ABCD")).IsEqualTo("/ID [<0000");
        // Literal string form (no closing ')'):
        await Assert.That(Normalize("/ID [(ABCD")).IsEqualTo("/ID [(0000");
    }

    [Test]
    public async Task NormalizedDocumentStillLoads()
    {
        var data = await File.ReadAllBytesAsync("sample.pdf");
        data = PdfNormalizer.Normalize(data);

        using var reader = DocLib.Instance.GetDocReader(data, new(scalingFactor: 2));
        await Assert.That(reader.GetPageCount()).IsEqualTo(2);
    }

    [Test]
    public async Task NeutralizesFopStyleXmp()
    {
        // sample-fop.pdf carries an uncompressed FOP-style XMP packet whose dc:date render time is
        // nested in rdf:Seq/rdf:li. It must be neutralized while the document still loads.
        var data = await File.ReadAllBytesAsync("sample-fop.pdf");
        data = PdfNormalizer.Normalize(data);

        var text = Encoding.Latin1.GetString(data);
        await Assert.That(text).Contains("<rdf:li>0000-00-00T00:00:00+00:00</rdf:li>");
        await Assert.That(text).DoesNotContain("2024-01-15");

        using var reader = DocLib.Instance.GetDocReader(data, new(scalingFactor: 2));
        await Assert.That(reader.GetPageCount()).IsEqualTo(1);
    }

    [Test]
    public async Task CanonicalizesXmpAcrossJreSerializers()
    {
        // The same FOP document rendered on two machines. Apache FOP serializes the XMP packet through
        // the platform's XML writer, so the JDK decides the indentation: one build emits a compact
        // packet, the other indents every element, and the raw bytes differ. Once normalized, the
        // output must collapse to identical bytes on both, and still load.
        var compactRaw = await File.ReadAllBytesAsync("sample-fop-compact.pdf");
        var indentedRaw = await File.ReadAllBytesAsync("sample-fop-indented.pdf");
        await Assert.That(compactRaw.SequenceEqual(indentedRaw)).IsFalse();

        var compact = PdfNormalizer.Normalize(compactRaw);
        var indented = PdfNormalizer.Normalize(indentedRaw);
        await Assert.That(compact).IsEquivalentTo(indented);

        using var reader = DocLib.Instance.GetDocReader(compact, new(scalingFactor: 2));
        await Assert.That(reader.GetPageCount()).IsEqualTo(1);
    }

    [Test]
    public async Task IsIdempotent()
    {
        // A second pass has nothing left to change: normalizing already-normalized bytes is a no-op.
        var once = PdfNormalizer.Normalize(await File.ReadAllBytesAsync("sample.pdf"));
        var twice = PdfNormalizer.Normalize(once);
        await Assert.That(twice).IsEquivalentTo(once);
    }

    [Test]
    public async Task NormalizedSinglePageSplitStillLoads()
    {
        // A page subset is re-serialized by pdfium (reintroducing volatile fields) then normalized;
        // it must remain a valid one-page document.
        var data = await File.ReadAllBytesAsync("sample.pdf");
        var split = DocLib.Instance.Split(data, 1, 1);
        split = PdfNormalizer.Normalize(split);

        using var reader = DocLib.Instance.GetDocReader(split, new(scalingFactor: 2));
        await Assert.That(reader.GetPageCount()).IsEqualTo(1);
    }

    [Test]
    public async Task DoesNotMutateTheInputArray()
    {
        // The byte[] overload is non-destructive: the caller keeps ownership of the buffer it passed.
        var data = await File.ReadAllBytesAsync("sample-fop.pdf");
        var original = (byte[]) data.Clone();

        PdfNormalizer.Normalize(data);

        await Assert.That(data).IsEquivalentTo(original);
    }

    [Test]
    public async Task StreamOverloadMatchesByteOverload()
    {
        var data = await File.ReadAllBytesAsync("sample-fop.pdf");
        var expected = PdfNormalizer.Normalize(data);

        using var source = new MemoryStream(data);
        // ReSharper disable once MethodHasAsyncOverload
        using var result = PdfNormalizer.Normalize(source);

        await Assert.That(result.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task StreamOverloadHonorsPosition()
    {
        // The document is preceded by unrelated bytes and the stream is positioned at the start of
        // the document, as it would be when reading from a container. Only the remainder is read.
        var data = await File.ReadAllBytesAsync("sample-fop.pdf");
        var expected = PdfNormalizer.Normalize(data);

        var prefix = "unrelated leading bytes"u8.ToArray();
        using var source = new MemoryStream([..prefix, ..data]);
        source.Position = prefix.Length;
        // ReSharper disable once MethodHasAsyncOverload
        using var result = PdfNormalizer.Normalize(source);

        await Assert.That(result.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task StreamOverloadHonorsPositionOnExposedBuffer()
    {
        // A MemoryStream that exposes its backing array (as produced by `new MemoryStream()` then
        // written to, which is how a caller hands over a freshly generated document) takes the
        // direct-copy fast path rather than the CopyTo fallback. It must honor Position too.
        var data = await File.ReadAllBytesAsync("sample-fop.pdf");
        var expected = PdfNormalizer.Normalize(data);

        var prefix = "unrelated leading bytes"u8.ToArray();
        using var source = new MemoryStream();
        source.Write(prefix, 0, prefix.Length);
        source.Write(data, 0, data.Length);
        source.Position = prefix.Length;

        // ReSharper disable once MethodHasAsyncOverload
        using var result = PdfNormalizer.Normalize(source);

        await Assert.That(result.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task StreamOverloadHonorsPositionConsistentlyAcrossStreamTypes()
    {
        // A MemoryStream and a FileStream positioned identically must produce the same result. The
        // MemoryStream fast path reads the backing array directly, so it has to respect Position
        // the same way the copy fallback does.
        var data = await File.ReadAllBytesAsync("sample-fop.pdf");
        var prefixed = new byte[8 + data.Length];
        Array.Copy(data, 0, prefixed, 8, data.Length);

        using var temp = await TempFile.CreateBinary(prefixed, ".pdf");

        using var memorySource = new MemoryStream(prefixed);
        memorySource.Position = 8;
        using var fromMemory = await PdfNormalizer.NormalizeAsync(memorySource);

        using var fileSource = File.OpenRead(temp.Path);
        fileSource.Position = 8;
        using var fromFile = await PdfNormalizer.NormalizeAsync(fileSource);

        await Assert.That(fromMemory.ToArray()).IsEquivalentTo(fromFile.ToArray());
    }

    [Test]
    public async Task AsyncProducesSameOutputAsSync()
    {
        var data = await File.ReadAllBytesAsync("sample-fop.pdf");
        var expected = PdfNormalizer.Normalize(data);

        // A non-seekable, non-MemoryStream source to exercise the copy path.
        using var source = File.OpenRead("sample-fop.pdf");
        using var result = await PdfNormalizer.NormalizeAsync(source);

        await Assert.That(result.ToArray()).IsEquivalentTo(expected);
    }

    static string Normalize(string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        bytes = PdfNormalizer.Normalize(bytes);
        return Encoding.Latin1.GetString(bytes);
    }
}
