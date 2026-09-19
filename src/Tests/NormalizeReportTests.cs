public class NormalizeReportTests
{
    [Test]
    public async Task NamesEachFieldItNeutralized()
    {
        var input =
            """
            /ID [<A1B2C3D4E5F60718> <1122334455667788>]
            /CreationDate(D:20240115093000+05'30')
            /ModDate(D:20240115093000Z)
            <xmp:CreateDate>2024-01-15T09:30:00+05:30</xmp:CreateDate>
            <xmpMM:DocumentID>uuid:0f7b2c9a-1234-5678-9abc-def012345678</xmpMM:DocumentID>
            """;

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
            """
            /ID [<A1B2C3D4E5F60718> <1122334455667788>]
            /ModDate(D:20240115093000Z)
            /ModDate(D:20250216104100Z)
            """);

        await Assert.That(changes.Single(_ => _.Name == "/ID").Count).IsEqualTo(2);
        await Assert.That(changes.Single(_ => _.Name == "/ModDate").Count).IsEqualTo(2);
    }

    // The sign of a time zone offset is a separator, so it outlives the digit zeroing. Two documents
    // whose dates are otherwise already zeroed can still differ by it alone, and that has to be
    // reported — it is exactly the kind of leftover the report exists to explain.
    [Test]
    public async Task ReportsASignOnlyChange()
    {
        var changes = Report(
            """
            /ModDate(D:00000000000000-00'00')
            <xmp:CreateDate>0000-00-00T00:00:00-00:00</xmp:CreateDate>
            """);

        await Assert.That(changes.Select(_ => _.Name)).IsEquivalentTo(["/ModDate", "xmp:CreateDate"]);
    }

    // A date whose digits are already zeroed can still differ by the *length* of its UTC offset, which
    // the collapse to "Z" is what fixes. That is a change like any other and has to be reported.
    [Test]
    public async Task ReportsACollapseOfTheTimeZoneAlone()
    {
        var data = DocumentBuilder.Build("D:00000000000000+00'00'", "0000-00-00T00:00:00+00:00");

        PdfNormalizer.Normalize(data, out var changes);

        await Assert.That(changes.Select(_ => _.Name)).Contains("/ModDate");
        await Assert.That(changes.Single(_ => _.Name == "xmp:CreateDate").Count).IsEqualTo(1);
    }

    // The collapse revisits the very dates the zeroing already reported. Count is occurrences altered,
    // not passes that touched them, so a date both passes changed still counts once.
    [Test]
    public async Task CountsADateZeroedAndCollapsedOnce()
    {
        var data = DocumentBuilder.Build("D:20240115093000+05'30'", "2024-01-15T09:30:00+05:30");

        PdfNormalizer.Normalize(data, out var changes);

        await Assert.That(changes.Single(_ => _.Name == "/ModDate").Count).IsEqualTo(1);
        await Assert.That(changes.Single(_ => _.Name == "xmp:CreateDate").Count).IsEqualTo(1);
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
            """
            /ID [<A1B2C3D4E5F60718>]
            /CreationDate(D:20240115093000+05'30')
            <xmp:CreateDate>2024-01-15T09:30:00Z</xmp:CreateDate>
            """;

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

    // The compact serialization is reported under the same names as the element form: the report
    // names the property, not the shape the producer happened to write it in.
    [Test]
    public async Task ReportsXmpAttributesUnderThePropertyName()
    {
        var changes = Report(
            """
            <rdf:Description xmp:CreateDate="2024-01-15T09:30:00Z" xmpMM:DocumentID="uuid:0f7b2c9a"/>
            """);

        await Assert.That(changes.Select(_ => _.Name))
            .IsEquivalentTo(["xmp:CreateDate", "xmpMM:DocumentID"]);
    }

    // A struct field is reported under its own name, so the report distinguishes "the document's own
    // modify date changed" from "a save event was appended to its history".
    [Test]
    public async Task ReportsResourceEventAndResourceRefFields()
    {
        var changes = Report(
            """
            <rdf:li stEvt:when="2026-09-17T17:21:19Z" stEvt:instanceID="xmp.iid:b0505ebe"/>
            <rdf:Description stRef:documentID="xmp.did:341e36e7"/>
            """);

        await Assert.That(changes.Select(_ => _.Name))
            .IsEquivalentTo(["stEvt:when", "stEvt:instanceID", "stRef:documentID"]);
    }

    [Test]
    public async Task ReportsTheXmpAttributeSample()
    {
        var data = await File.ReadAllBytesAsync("sample-xmp-attributes.pdf");

        PdfNormalizer.Normalize(data, out var changes);

        var names = changes.Select(_ => _.Name).ToList();
        await Assert.That(names).Contains("xmp:CreateDate");
        await Assert.That(names).Contains("xmpMM:InstanceID");
        await Assert.That(names).Contains("XMP packet whitespace");
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
