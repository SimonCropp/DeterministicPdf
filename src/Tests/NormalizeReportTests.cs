public class NormalizeReportTests
{
    [Test]
    public async Task NamesEachFieldItNeutralized()
    {
        var input =
            "/ID [<A1B2C3D4E5F60718> <1122334455667788>] " +
            "/CreationDate(D:20240115093000+05'30') " +
            "/ModDate(D:20240115093000Z) " +
            "<xmp:CreateDate>2024-01-15T09:30:00+05:30</xmp:CreateDate>" +
            "<xmpMM:DocumentID>uuid:0f7b2c9a-1234-5678-9abc-def012345678</xmpMM:DocumentID>";

        var changes = Report(input);

        await Assert.That(changes.Select(_ => _.Name))
            .IsEquivalentTo(["/CreationDate", "/ModDate", "/ID", "xmp:CreateDate", "xmpMM:DocumentID"]);
    }

    // The order is the order the passes run, not the order the values happen to appear in the
    // document: /ID is written before /CreationDate above but reported after it.
    [Test]
    public async Task ReportsInPassOrder()
    {
        var changes = Report("/ID [<A1B2>] /CreationDate(D:20240115093000Z)");

        await Assert.That(changes.Select(_ => _.Name)).IsEquivalentTo(["/CreationDate", "/ID"]);
    }

    [Test]
    public async Task CountsEveryOccurrence()
    {
        var changes = Report(
            "/ID [<A1B2C3D4E5F60718> <1122334455667788>] " +
            "/ModDate(D:20240115093000Z) " +
            "/ModDate(D:20250216104100Z)");

        await Assert.That(changes.Single(_ => _.Name == "/ID").Count).IsEqualTo(2);
        await Assert.That(changes.Single(_ => _.Name == "/ModDate").Count).IsEqualTo(2);
    }

    [Test]
    public async Task ReportsNothingForContentWithNoVolatileValues() =>
        await Assert.That(Report("/Type /Page /Contents 4 0 R")).IsEmpty();

    // The property the whole report hangs on. A pass that runs over already zeroed content has not
    // changed the document, so it must not be reported — otherwise a caller could never use the
    // report to answer "why is this file not deterministic?".
    [Test]
    public async Task ReportsNothingOnASecondNormalization()
    {
        var input =
            "/ID [<A1B2C3D4E5F60718>] " +
            "/CreationDate(D:20240115093000+05'30') " +
            "<xmp:CreateDate>2024-01-15T09:30:00Z</xmp:CreateDate>";

        var once = PdfNormalizer.Normalize(Encoding.Latin1.GetBytes(input));

        PdfNormalizer.Normalize(once, out var changes);

        await Assert.That(changes).IsEmpty();
    }

    [Test]
    public async Task ReportsTheSameFieldsForARealDocument()
    {
        var data = await File.ReadAllBytesAsync("sample.pdf");

        PdfNormalizer.Normalize(data, out var changes);

        await Assert.That(changes).IsNotEmpty();
        await Assert.That(changes.Select(_ => _.Name)).Contains("/CreationDate");
    }

    // sample-fop.pdf is the one with an indented XMP packet, so it exercises the canonicalization
    // pass — the only reported change that is not a zeroed value.
    [Test]
    public async Task ReportsXmpCanonicalization()
    {
        var data = await File.ReadAllBytesAsync("sample-fop-indented.pdf");

        PdfNormalizer.Normalize(data, out var changes);

        await Assert.That(changes.Select(_ => _.Name)).Contains("XMP packet whitespace");
    }

    [Test]
    public async Task ReportsDublinCoreDate()
    {
        var data = await File.ReadAllBytesAsync("sample-fop.pdf");

        PdfNormalizer.Normalize(data, out var changes);

        await Assert.That(changes.Select(_ => _.Name)).Contains("dc:date");
    }

    [Test]
    public async Task TheReportingOverloadProducesIdenticalBytes()
    {
        var data = await File.ReadAllBytesAsync("sample-fop.pdf");

        var plain = PdfNormalizer.Normalize(data);
        var reported = PdfNormalizer.Normalize(data, out _);

        await Assert.That(reported).IsEquivalentTo(plain);
    }

    [Test]
    public async Task TheStreamOverloadReportsTheSameChanges()
    {
        var data = await File.ReadAllBytesAsync("sample-fop.pdf");
        PdfNormalizer.Normalize(data, out var fromBytes);

        using var source = new MemoryStream(data);
        using var result = PdfNormalizer.Normalize(source, out var fromStream);

        await Assert.That(fromStream.Select(_ => _.Name)).IsEquivalentTo(fromBytes.Select(_ => _.Name));
        await Assert.That(result.ToArray()).IsEquivalentTo(PdfNormalizer.Normalize(data));
    }

    static IReadOnlyList<NormalizeChange> Report(string value)
    {
        PdfNormalizer.Normalize(Encoding.Latin1.GetBytes(value), out var changes);
        return changes;
    }
}
