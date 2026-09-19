// Builds a minimal but complete one-page PDF - a catalog, a page tree, a page, an information
// dictionary and an uncompressed XMP metadata stream, with a correct cross-reference table - carrying
// whatever dates the caller asks for, plus an optional extra body object.
//
// Spelling the dates is the whole point. A producer writes the UTC offset as "Z" on a machine running
// in UTC and as "+05'30'" anywhere else, so the two renders of one document are *different lengths*:
// no length-preserving edit can ever reconcile them, and a pair built here is the only way to prove
// that the rewrite which can, does.
static class DocumentBuilder
{
    public static byte[] Build(string pdfDate, string xmpDate, string? extraObject = null)
    {
        var packet =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">" +
            $"<xmp:CreateDate>{xmpDate}</xmp:CreateDate>" +
            $"<xmp:ModifyDate>{xmpDate}</xmp:ModifyDate>" +
            "</rdf:Description>" +
            "</rdf:RDF>" +
            "</x:xmpmeta>" +
            "<?xpacket end=\"w\"?>";

        // Apache FOP counts the end-of-line after the packet in the stream length, so the sample does
        // too: it is what makes the metadata /Length worth repairing.
        var content = packet + "\n";

        List<string> objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /Metadata 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
            $"<< /Producer (Test) /CreationDate ({pdfDate}) /ModDate ({pdfDate}) >>",
            $"<< /Type /Metadata /Subtype /XML /Length {content.Length} >>\nstream\n{content}endstream"
        ];

        if (extraObject != null)
        {
            objects.Add(extraObject);
        }

        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var number = 1; number <= objects.Count; number++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{number} 0 obj\n{objects[number - 1]}\nendobj\n");
        }

        var xref = builder.Length;
        builder.Append($"xref\n0 {objects.Count + 1}\n");
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append(
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 4 0 R " +
            "/ID [<A1B2C3D4E5F60718> <1122334455667788>] >>\n" +
            $"startxref\n{xref}\n%%EOF\n");

        // Every character written above is ASCII, so the lengths the offsets were taken from are byte
        // counts and the table is correct as built.
        return Encoding.Latin1.GetBytes(builder.ToString());
    }
}
