namespace DeterministicPdf;

/// <summary>
/// Neutralizes the non-deterministic fields of a PDF (the trailer <c>/ID</c>, the document
/// information <c>/CreationDate</c> and <c>/ModDate</c>, the page and page-piece
/// <c>/LastModified</c>, and the equivalent XMP metadata dates and identifiers) so that the same
/// source document always produces byte-identical output.
/// </summary>
/// <remarks>
/// Neutralizing the values (dates and identifiers) is done in place and is length-preserving: only the
/// mutable characters inside each value are overwritten, so every cross-reference offset stays valid. A
/// value that has been compressed away (inside an <c>/ObjStm</c> object stream or a flate-compressed XMP
/// packet) no longer appears literally and is therefore left as-is.
/// <para>
/// The UTC offset of a date is then collapsed to <c>Z</c>. A producer spells it <c>Z</c> on a machine
/// running in UTC and <c>+10:30</c> anywhere else, which are different lengths, so no length-preserving
/// edit could reconcile two such renders. Because this shortens the document, the cross-reference table
/// is repaired afterwards, just as it is for the packet rewrite below.
/// </para>
/// <para>
/// The XMP packet is then canonicalized. Apache FOP serializes it through the platform's XML writer, so
/// the same document is indented differently depending on which JRE produced it; once the values are
/// zeroed, that whitespace is the only remaining cross-platform difference. It is collapsed to a single
/// canonical form. Because that changes the packet length, the metadata stream length and the classic
/// cross-reference table are repaired afterwards, which is why the method returns the resulting buffer.
/// A document that cannot be safely rewritten this way (a cross-reference stream, an incremental update,
/// or an unlocatable stream length) is returned unchanged.
/// </para>
/// <para>
/// Targets unencrypted documents. Encrypted PDFs seed their encryption key from the trailer
/// <c>/ID</c>; zeroing it would leave the document undecryptable, so encrypted input should not be
/// passed here.
/// </para>
/// </remarks>
public static partial class PdfNormalizer
{
    enum Fill
    {
        // Zero the ASCII digits only, keeping separators (leaves a readable date), and force the sign
        // of a time zone offset to '+' — it is a separator too, so zeroing alone would leave it
        // recording which side of Greenwich the render happened on.
        Digits,

        // Zero the hexadecimal digits (for hex string <...> values).
        Hex,

        // Zero every non-whitespace byte (for opaque identifiers).
        All
    }

    // A date value one of the zeroing passes neutralized: where it ended up, what the report calls it,
    // and whether the zeroing already reported this occurrence — so the time zone rewrite, which runs
    // over the same values afterwards, does not count one occurrence twice.
    record struct NeutralizedDate(byte[] Name, int Start, int End, bool Recorded);

    /// <summary>
    /// Returns a normalized copy of <paramref name="data"/>. The input array is not modified.
    /// </summary>
    public static byte[] Normalize(byte[] data)
    {
        // The passes below overwrite bytes in place, so work on a copy: a public caller keeps
        // ownership of the buffer it passed in.
        var copy = new byte[data.Length];
        Array.Copy(data, copy, data.Length);
        return NormalizeCore(copy, new());
    }

    /// <summary>
    /// Returns a normalized copy of <paramref name="data"/> and reports what was altered. The input
    /// array is not modified.
    /// </summary>
    /// <param name="data">The document to normalize.</param>
    /// <param name="changes">
    /// What actually differed, in the order the passes run. Empty when the document was already
    /// normalized, since a pass records only when bytes really changed rather than merely because it
    /// ran — which is what makes this usable as the reason a document is not yet deterministic.
    /// </param>
    public static byte[] Normalize(byte[] data, out IReadOnlyList<NormalizeChange> changes)
    {
        var copy = new byte[data.Length];
        Array.Copy(data, copy, data.Length);
        var recorder = new ChangeRecorder();
        var result = NormalizeCore(copy, recorder);
        changes = recorder.Changes;
        return result;
    }

    // Normalizes 'data' in place, returning either the same array or (when a length-changing rewrite
    // applies, which the time zone and XMP packet passes both are) a freshly built one.
    static byte[] NormalizeCore(byte[] data, ChangeRecorder recorder)
    {
        // Where every date the passes below neutralize ends up. The time zone rewrite has to find them
        // again, and taking them from the passes themselves is what stops a second list of date keys
        // drifting out of step with this one.
        var dates = new List<NeutralizedDate>();

        // Document information dictionary dates.
        ZeroPdfString(data, "/CreationDate"u8, Fill.Digits, recorder, dates);
        ZeroPdfString(data, "/ModDate"u8, Fill.Digits, recorder, dates);

        // Page and page-piece dictionary modification date. A producer stamps a wall-clock time here
        // for its own private data, so it changes on every render even when nothing about the document
        // did (PDFTron writes one onto the form XObject it uses for a watermark:
        // /PieceInfo<</PDFTRON<</LastModified(D:...)/Private/Watermark>>>>).
        ZeroPdfString(data, "/LastModified"u8, Fill.Digits, recorder, dates);

        // Trailer / cross-reference-stream file identifier: /ID [<...> <...>].
        ZeroFileId(data, recorder);

        // XMP metadata dates (uncompressed metadata streams only).
        ZeroXmpProperty(data, "<xmp:CreateDate"u8, Fill.Digits, recorder, dates);
        ZeroXmpProperty(data, "<xmp:ModifyDate"u8, Fill.Digits, recorder, dates);
        ZeroXmpProperty(data, "<xmp:MetadataDate"u8, Fill.Digits, recorder, dates);

        // Dublin Core date. Unlike the xmp:* dates above it is an ordered array (seq Date), so the
        // value is nested inside rdf:Seq/rdf:li rather than being direct text content of the element
        // (this is what Apache FOP emits). An array is also the one shape that has no attribute form,
        // which is why this is the only XMP pass with no ZeroXmpProperty counterpart.
        ZeroXmpElementTree(data, "<dc:date"u8, "</dc:date>"u8, Fill.Digits, recorder, dates);

        // XMP per-generation identifiers.
        ZeroXmpProperty(data, "<xmpMM:DocumentID"u8, Fill.All, recorder, dates);
        ZeroXmpProperty(data, "<xmpMM:InstanceID"u8, Fill.All, recorder, dates);
        ZeroXmpProperty(data, "<xmpMM:OriginalDocumentID"u8, Fill.All, recorder, dates);

        // The volatile fields of the structs the identifiers above are referenced from: the
        // ResourceEvent (stEvt) entries of xmpMM:History and the ResourceRef (stRef) of
        // xmpMM:DerivedFrom. A producer that appends a save event to the history stamps a fresh
        // stEvt:when and stEvt:instanceID onto it on every render.
        ZeroXmpProperty(data, "<stEvt:when"u8, Fill.Digits, recorder, dates);
        ZeroXmpProperty(data, "<stEvt:instanceID"u8, Fill.All, recorder, dates);
        ZeroXmpProperty(data, "<stRef:instanceID"u8, Fill.All, recorder, dates);
        ZeroXmpProperty(data, "<stRef:documentID"u8, Fill.All, recorder, dates);
        ZeroXmpProperty(data, "<stRef:originalDocumentID"u8, Fill.All, recorder, dates);
        ZeroXmpProperty(data, "<stRef:lastModifyDate"u8, Fill.Digits, recorder, dates);

        // Collapse every UTC offset to "Z". A producer that spells the offset "Z" on a machine in UTC
        // and "+10:30" elsewhere writes dates of different *lengths*, which no amount of zeroing can
        // reconcile, so this one is a length-changing rewrite with the same cross-reference repair the
        // packet rewrite below needs. It records the dates it collapsed itself, since it is the only
        // pass that knows which of them were already reported by the zeroing above.
        data = CanonicalizeDateZones(data, dates, recorder);

        // Collapse the JRE-dependent XMP packet whitespace and repair the cross-reference table so the
        // output is byte-identical across platforms. This can shrink the buffer, so the result of the
        // rewrite (a new buffer, or the same one when there is nothing to do) is returned to the caller.
        //
        // Every bail-out path hands back the input array itself, and so does the already-canonical
        // case, so reference equality is exactly the test for "this pass did something".
        var canonicalized = CanonicalizeXmp(data);
        if (!ReferenceEquals(canonicalized, data))
        {
            recorder.Record("XMP packet whitespace");
        }

        return canonicalized;
    }

    // Finds a name key, then overwrites the string value that follows it. The value may be a
    // literal string "(...)" or a hex string "<...>".
    static void ZeroPdfString(byte[] data, ReadOnlySpan<byte> key, Fill fill, ChangeRecorder recorder, List<NeutralizedDate> dates)
    {
        var pos = 0;
        while (true)
        {
            var hit = data.AsSpan(pos).IndexOf(key);
            if (hit < 0)
            {
                return;
            }

            var i = pos + hit + key.Length;
            pos = i;

            i = SkipWhitespace(data, i);
            if (i >= data.Length)
            {
                return;
            }

            if (data[i] == (byte) '(')
            {
                var start = i + 1;
                var end = FindLiteralEnd(data, start);
                Neutralize(data, start, end, fill, key, recorder, dates);
                pos = end;
            }
            else if (data[i] == (byte) '<' && (i + 1 >= data.Length || data[i + 1] != (byte) '<'))
            {
                // A hex-encoded date is not left for the time zone pass: its characters are the
                // encoding, not the date, so there is nothing there to recognize a UTC offset by.
                var start = i + 1;
                var end = FindByte(data, start, (byte) '>');
                if (Overwrite(data, start, end, Fill.Hex))
                {
                    recorder.Record(key);
                }

                pos = end;
            }
        }
    }

    // Finds "/ID" followed by an array and zeroes each string element. Anything not shaped like the
    // identifier array (for example the "/IDTree" name-tree key) is skipped.
    static void ZeroFileId(byte[] data, ChangeRecorder recorder)
    {
        var key = "/ID"u8;
        var pos = 0;
        while (true)
        {
            var hit = data.AsSpan(pos).IndexOf(key);
            if (hit < 0)
            {
                return;
            }

            var i = pos + hit + key.Length;
            pos = i;

            i = SkipWhitespace(data, i);
            if (i >= data.Length || data[i] != (byte) '[')
            {
                continue;
            }

            i++;
            while (i < data.Length && data[i] != (byte) ']')
            {
                if (data[i] == (byte) '<')
                {
                    var start = i + 1;
                    i = FindByte(data, start, (byte) '>');
                    if (Overwrite(data, start, i, Fill.Hex))
                    {
                        recorder.Record(key);
                    }

                    if (i < data.Length)
                    {
                        i++;
                    }
                }
                else if (data[i] == (byte) '(')
                {
                    var start = i + 1;
                    i = FindLiteralEnd(data, start);
                    if (Overwrite(data, start, i, Fill.All))
                    {
                        recorder.Record(key);
                    }

                    if (i < data.Length)
                    {
                        i++;
                    }
                }
                else
                {
                    i++;
                }
            }

            pos = i;
        }
    }

    // Zeroes an XMP property in both of the RDF serializations a producer may have used: as an element
    // of its own, and in the compact form that carries it as an attribute of the enclosing
    // rdf:Description or rdf:li. A document uses one or the other, so at most one of the two finds
    // anything, and both report under the same name: the report names the property, not the shape the
    // producer happened to write it in.
    static void ZeroXmpProperty(byte[] data, ReadOnlySpan<byte> openTag, Fill fill, ChangeRecorder recorder, List<NeutralizedDate> dates)
    {
        ZeroXmpElement(data, openTag, fill, recorder, dates);
        ZeroXmpAttribute(data, openTag[1..], fill, recorder, dates);
    }

    // Finds an XMP element by its opening tag and zeroes the text content up to the next '<'.
    static void ZeroXmpElement(byte[] data, ReadOnlySpan<byte> openTag, Fill fill, ChangeRecorder recorder, List<NeutralizedDate> dates)
    {
        var pos = 0;
        while (true)
        {
            var start = NextXmpElementContent(data, openTag, ref pos);
            if (start < 0)
            {
                return;
            }

            // Past the '<', so the report names the element rather than its opening tag.
            var end = FindByte(data, start, (byte) '<');
            Neutralize(data, start, end, fill, openTag[1..], recorder, dates);
            pos = end;
        }
    }

    // Like ZeroXmpElement, but descends through child markup and zeroes the content of every text
    // node up to the matching close tag. XMP array properties (for example dc:date, a "seq Date")
    // wrap their value in an rdf:Seq/rdf:li list, so the volatile value is not direct text content of
    // the named element and ZeroXmpElement alone would step over it.
    static void ZeroXmpElementTree(byte[] data, ReadOnlySpan<byte> openTag, ReadOnlySpan<byte> closeTag, Fill fill, ChangeRecorder recorder, List<NeutralizedDate> dates)
    {
        var pos = 0;
        while (true)
        {
            var start = NextXmpElementContent(data, openTag, ref pos);
            if (start < 0)
            {
                return;
            }

            var closeHit = data.AsSpan(start).IndexOf(closeTag);
            if (closeHit < 0)
            {
                return;
            }

            var end = start + closeHit;
            var i = start;
            while (i < end)
            {
                // Skip markup so only text nodes are altered, never element or attribute names.
                if (data[i] == (byte) '<')
                {
                    i = FindByte(data, i, (byte) '>');
                    if (i < end)
                    {
                        i++;
                    }

                    continue;
                }

                var textEnd = FindByte(data, i, (byte) '<');
                Neutralize(data, i, textEnd, fill, openTag[1..], recorder, dates);
                i = textEnd;
            }

            pos = end;
        }
    }

    // Locates the next element whose opening tag is 'openTag', returning the index of its content
    // (the byte after '>'), or -1 when no further match exists. A longer look-alike name or a
    // self-closing tag is skipped internally. 'pos' is advanced past the opening tag so scanning can
    // resume from the returned index.
    static int NextXmpElementContent(byte[] data, ReadOnlySpan<byte> openTag, ref int pos)
    {
        while (true)
        {
            var hit = data.AsSpan(pos).IndexOf(openTag);
            if (hit < 0)
            {
                return -1;
            }

            var i = pos + hit + openTag.Length;
            pos = i;

            // Reject a longer element name that merely shares this prefix.
            if (i < data.Length && data[i] != (byte) '>' && data[i] != (byte) '/' && !IsWhitespace(data[i]))
            {
                continue;
            }

            // Skip the remainder of the opening tag, remembering the last significant byte so a
            // self-closing "<tag/>" can be detected.
            var lastSignificant = (byte) 0;
            while (i < data.Length && data[i] != (byte) '>')
            {
                if (!IsWhitespace(data[i]))
                {
                    lastSignificant = data[i];
                }

                i++;
            }

            if (i >= data.Length)
            {
                return -1;
            }

            i++;
            pos = i;
            if (lastSignificant == (byte) '/')
            {
                continue;
            }

            return i;
        }
    }

    // Finds an XMP property written as an attribute rather than an element and zeroes the quoted
    // value. The compact RDF serialization carries a simple property as an attribute of
    // rdf:Description (xmp:CreateDate="2024-01-15T09:30:00Z") rather than as an element, and
    // ZeroXmpElement, which matches on a '<' followed by the name, steps straight over that.
    static void ZeroXmpAttribute(byte[] data, ReadOnlySpan<byte> name, Fill fill, ChangeRecorder recorder, List<NeutralizedDate> dates)
    {
        var pos = 0;
        while (true)
        {
            var hit = data.AsSpan(pos).IndexOf(name);
            if (hit < 0)
            {
                return;
            }

            var nameStart = pos + hit;
            var i = nameStart + name.Length;
            pos = i;

            // XML requires whitespace before an attribute name, so anything else preceding it is a
            // different construct: the element form "<xmp:CreateDate", its closing tag, or a longer
            // name that merely ends with this one.
            if (nameStart == 0 ||
                !IsWhitespace(data[nameStart - 1]))
            {
                continue;
            }

            // Whitespace is permitted either side of the '='. Requiring that '=' is also what rejects
            // a longer name merely starting with this one, whose next byte is a name character.
            i = SkipWhitespace(data, i);
            if (i >= data.Length ||
                data[i] != (byte) '=')
            {
                continue;
            }

            i = SkipWhitespace(data, i + 1);
            if (i >= data.Length)
            {
                return;
            }

            // Either quote character may delimit an attribute value, and the value cannot contain the
            // one that opened it, so the next occurrence closes it.
            var quote = data[i];
            if (quote != (byte) '"' &&
                quote != (byte) '\'')
            {
                continue;
            }

            var start = i + 1;
            var end = FindByte(data, start, quote);
            Neutralize(data, start, end, fill, name, recorder, dates);
            pos = end;
        }
    }

    // Neutralizes one value: overwrites it, reports it under 'name' when bytes really differed, and,
    // when the value is a date, remembers where it ended up so the time zone rewrite can pick it up
    // without a second list of date keys of its own.
    static void Neutralize(byte[] data, int start, int end, Fill fill, ReadOnlySpan<byte> name, ChangeRecorder recorder, List<NeutralizedDate> dates)
    {
        var changed = Overwrite(data, start, end, fill);
        if (changed)
        {
            recorder.Record(name);
        }

        if (fill == Fill.Digits)
        {
            dates.Add(new(name.ToArray(), start, end, changed));
        }
    }

    // Returns whether any byte actually differed. The report is built from that rather than from the
    // pass having run: writing '0' over a '0' leaves the document alone, and a second normalization
    // of the same document must report nothing.
    static bool Overwrite(byte[] data, int start, int end, Fill fill)
    {
        var changed = false;
        for (var i = start; i < end; i++)
        {
            var c = data[i];
            var replace = fill switch
            {
                Fill.Digits => IsDigit(c),
                Fill.Hex => IsHexDigit(c),
                _ => !IsWhitespace(c)
            };
            if (replace &&
                c != (byte) '0')
            {
                data[i] = (byte) '0';
                changed = true;
            }
        }

        if (fill == Fill.Digits &&
            CanonicalizeTimeZoneSign(data, start, end))
        {
            changed = true;
        }

        return changed;
    }

    // Forces the sign of a time zone offset to '+'. The sign is a separator, so zeroing the digits
    // leaves it in place, and it is then the one part of the date still recording where the render
    // happened: the same instant keeps "+00:00" east of Greenwich and "-00:00" west of it. Both now
    // denote the same zeroed instant, and '+' is how ISO 8601 spells a zero offset ("-00:00" is not
    // even a legal spelling there).
    //
    // CanonicalizeDateZones goes on to collapse the whole offset to "Z", which subsumes this — but
    // only for documents it can safely rewrite. This is the length-preserving half, and it always
    // applies.
    static bool CanonicalizeTimeZoneSign(byte[] data, int start, int end)
    {
        if (!TryFindTimeZone(data, start, end, out var zone) ||
            data[zone.start] != (byte) '-')
        {
            return false;
        }

        data[zone.start] = (byte) '+';
        return true;
    }

    // Locates the time zone designator of the date in [start, end): the trailing "Z", or the
    // "+hh:mm" offset of an ISO 8601 date and the "+hh'mm'" offset of a PDF date string. Returns
    // false when the value carries none — a date with no time at all (dc:date permits one), or a
    // local time written without a zone.
    static bool TryFindTimeZone(byte[] data, int start, int end, out (int start, int end) zone)
    {
        zone = default;
        for (var i = start; i < end; i++)
        {
            // Only a designator that follows the time is one. The ISO 8601 form XMP uses spells the
            // date itself "0000-00-00", so the '-' of a designator cannot be told from a separator by
            // shape alone. The time is introduced by the 'T' there, and by the "D:" prefix of a PDF
            // date string (which carries no other sign). The whole span is walked rather than just
            // its tail because a text node is not always a lone date.
            if (data[i] != (byte) 'T' &&
                (data[i] != (byte) 'D' || i + 1 >= end || data[i + 1] != (byte) ':'))
            {
                continue;
            }

            // Scanning stops at the first byte that cannot belong to a time, so the markup or
            // punctuation after a date is never run past into whatever follows it.
            for (i++; i < end; i++)
            {
                var ch = data[i];
                if (ch is (byte) 'Z' or (byte) '+' or (byte) '-')
                {
                    var designatorEnd = i + 1;
                    while (designatorEnd < end &&
                           (IsDigit(data[designatorEnd]) || data[designatorEnd] is (byte) ':' or (byte) '\''))
                    {
                        designatorEnd++;
                    }

                    zone = (i, designatorEnd);
                    return true;
                }

                if (!IsDigit(ch) &&
                    ch is not ((byte) ':' or (byte) '.'))
                {
                    break;
                }
            }
        }

        return false;
    }

    // Returns the index of the ')' that closes the literal string starting at 'start', honoring
    // backslash escapes and balanced parentheses, or the end of the buffer if unterminated.
    static int FindLiteralEnd(byte[] data, int start)
    {
        var depth = 1;
        var i = start;
        while (i < data.Length)
        {
            var c = data[i];
            if (c == (byte) '\\')
            {
                i += 2;
                continue;
            }

            if (c == (byte) '(')
            {
                depth++;
            }
            else if (c == (byte) ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            i++;
        }

        return data.Length;
    }

    static int FindByte(byte[] data, int start, byte target)
    {
        var i = start;
        while (i < data.Length && data[i] != target)
        {
            i++;
        }

        return i;
    }

    static int SkipWhitespace(byte[] data, int i)
    {
        while (i < data.Length && IsWhitespace(data[i]))
        {
            i++;
        }

        return i;
    }

    static bool IsDigit(byte b) =>
        b is >= (byte) '0' and <= (byte) '9';

    static bool IsHexDigit(byte b) =>
        b is >= (byte) '0' and <= (byte) '9' or >= (byte) 'a' and <= (byte) 'f' or >= (byte) 'A' and <= (byte) 'F';

    static bool IsWhitespace(byte b) =>
        b is (byte) ' ' or (byte) '\t' or (byte) '\r' or (byte) '\n' or (byte) '\f' or 0;

    // Collapses the time zone designator of every neutralized date to "Z" and repairs the classic
    // cross-reference table so the offsets stay valid.
    //
    // The zeroing passes cannot reach this one: a producer that spells the offset "Z" on a machine in
    // UTC and "+10:30" elsewhere writes dates of different lengths, so the two renders differ in length
    // however their digits are overwritten. The only fix is to make the designator itself a fixed
    // spelling, which changes the length of the document and so needs the same repair the packet
    // rewrite needs. "Z" is chosen because it is the shortest, so every edit shortens the document and
    // no date that lacks an offset has to grow one.
    //
    // Returns the input array untouched when there is nothing to collapse, or when the document is not a
    // shape this can safely rewrite - the in-place zeroing above still stands in that case, and with it
    // CanonicalizeTimeZoneSign, which is as far as a length-preserving edit can get.
    static byte[] CanonicalizeDateZones(byte[] data, List<NeutralizedDate> dates, ChangeRecorder recorder)
    {
        // A rewrite inside stream data changes that stream's length. The metadata stream is the one
        // whose length this can restate (it is the one the XMP dates live in), so an edit landing in
        // any other is dropped rather than left to break the document.
        var streams = FindStreamDataRegions(data);
        var packetStart = IndexOf(data, "<?xpacket begin"u8, 0);
        var metadata = (start: -1, end: -1);
        foreach (var stream in streams)
        {
            if (Contains(stream, packetStart))
            {
                metadata = stream;
                break;
            }
        }

        var edits = new List<(int start, int end, byte[] replacement)>();
        var collapsed = new List<NeutralizedDate>();
        var metadataDelta = 0;
        foreach (var date in dates)
        {
            if (!TryFindTimeZone(data, date.Start, date.End, out var zone) ||
                zone.end - zone.start == 1 && data[zone.start] == (byte) 'Z')
            {
                continue;
            }

            if (IsInsideOtherStream(streams, metadata, zone.start))
            {
                continue;
            }

            edits.Add((zone.start, zone.end, "Z"u8.ToArray()));
            collapsed.Add(date);
            if (Contains(metadata, zone.start))
            {
                metadataDelta += 1 - (zone.end - zone.start);
            }
        }

        if (edits.Count == 0)
        {
            return data;
        }

        if (!TryReadClassicXref(data, out var xrefKeyword, out var entries, out var startxref))
        {
            return data;
        }

        // Shortening the packet means restating the metadata stream's declared length.
        if (metadataDelta != 0)
        {
            if (!TryFindStreamLength(data, packetStart, out var lengthField))
            {
                return data;
            }

            var cursor = lengthField.start;
            if (!TryReadInt(data, ref cursor, out var length))
            {
                return data;
            }

            edits.Add((lengthField.start, lengthField.end, AsciiDigits(length + metadataDelta)));
        }

        edits.Sort((left, right) => left.start.CompareTo(right.start));

        // startxref points at the cross-reference keyword, which trails every edit above, so it is
        // repointed to the shifted position. That edit sits after the table and so does not move it.
        var all = new List<(int start, int end, byte[] replacement)>(edits)
        {
            (startxref.start, startxref.end, AsciiDigits(Shift(edits, xrefKeyword)))
        };

        var rebuilt = ApplyEdits(data, all);

        // Repair each in-use entry with the shifted offset of its object. The entry field and the object
        // it points at are both original positions, mapped through the same shift.
        foreach (var entry in entries)
        {
            if (entry.inUse)
            {
                WriteOffset(rebuilt, Shift(edits, entry.fieldOffset), Shift(edits, entry.objectOffset));
            }
        }

        // Only now that the rewrite is certain, and only for occurrences the zeroing did not already
        // report: the count is occurrences altered, not passes that touched them.
        foreach (var date in collapsed)
        {
            if (!date.Recorded)
            {
                recorder.Record(date.Name);
            }
        }

        return rebuilt;
    }

    // The data region of every stream in the document. A region that cannot be closed ends the scan, so
    // the result is conservative by construction: it is used to reject edits, never to license them.
    static List<(int start, int end)> FindStreamDataRegions(byte[] data)
    {
        var regions = new List<(int start, int end)>();
        var position = 0;
        while (true)
        {
            var keyword = FindStreamKeyword(data, position);
            if (keyword < 0)
            {
                return regions;
            }

            var start = SkipEol(data, keyword + "stream"u8.Length);
            var end = IndexOf(data, "endstream"u8, start);
            if (end < 0)
            {
                return regions;
            }

            regions.Add((start, end));
            position = end + "endstream"u8.Length;
        }
    }

    // Finds the next "stream" keyword that opens a stream rather than the tail of an "endstream".
    static int FindStreamKeyword(byte[] data, int position)
    {
        while (true)
        {
            var hit = IndexOf(data, "stream"u8, position);
            if (hit < 0)
            {
                return -1;
            }

            position = hit + "stream"u8.Length;
            if (hit < 3 || !StartsWith(data, hit - 3, "end"u8))
            {
                return hit;
            }
        }
    }

    static bool IsInsideOtherStream(List<(int start, int end)> streams, (int start, int end) metadata, int position)
    {
        foreach (var stream in streams)
        {
            if (Contains(stream, position) &&
                stream != metadata)
            {
                return true;
            }
        }

        return false;
    }

    static bool Contains((int start, int end) region, int position) =>
        position >= region.start &&
        position < region.end;

    // Maps an original byte position, which must not itself sit inside an edited region, to where it
    // ends up once every edit is applied.
    static int Shift(List<(int start, int end, byte[] replacement)> edits, int position)
    {
        var shifted = position;
        foreach (var edit in edits)
        {
            if (position >= edit.end)
            {
                shifted += edit.replacement.Length - (edit.end - edit.start);
            }
        }

        return shifted;
    }

    // Collapses the whitespace of the single XMP metadata packet to a canonical form and repairs the
    // classic cross-reference table so the offsets stay valid. Returns the original array untouched when
    // there is nothing to canonicalize, or when the document is not a shape this can safely rewrite: no
    // packet, more than one packet, a cross-reference stream, more than one cross-reference section, or a
    // metadata stream whose length cannot be located.
    static byte[] CanonicalizeXmp(byte[] data)
    {
        var packetStart = IndexOf(data, "<?xpacket begin"u8, 0);
        if (packetStart < 0)
        {
            return data;
        }

        // Only a single packet is handled.
        if (IndexOf(data, "<?xpacket begin"u8, packetStart + 1) >= 0)
        {
            return data;
        }

        var endTag = IndexOf(data, "<?xpacket end"u8, packetStart);
        if (endTag < 0)
        {
            return data;
        }

        var closeMarker = IndexOf(data, "?>"u8, endTag);
        if (closeMarker < 0)
        {
            return data;
        }

        // The stream content runs from the packet up to endstream. The bytes between the packet's closing
        // marker and endstream are a platform-dependent end-of-line (Apache FOP emits it with the JRE's
        // line separator and counts it in the stream length), so they are folded into the region and
        // normalized too. They must be whitespace for the whole content region to be replaced wholesale.
        var packetEnd = closeMarker + 2;
        var contentEnd = IndexOf(data, "endstream"u8, packetEnd);
        if (contentEnd < 0 || !IsAllWhitespace(data, packetEnd, contentEnd))
        {
            return data;
        }

        // Canonical content: the packet with its inter-element whitespace collapsed, then a single
        // normalized end-of-line before endstream.
        var collapsed = CollapseInterTagWhitespace(data, packetStart, packetEnd);
        var canonical = new byte[collapsed.Length + 1];
        Array.Copy(collapsed, canonical, collapsed.Length);
        canonical[collapsed.Length] = (byte) '\n';

        if (canonical.Length == contentEnd - packetStart)
        {
            // Already canonical: leave the bytes (and the cross-reference table) untouched. This keeps
            // the pass idempotent and avoids rewriting documents that do not need it.
            return data;
        }

        if (!TryReadClassicXref(data, out var xrefKeyword, out var entries, out var startxref))
        {
            return data;
        }

        if (!TryFindStreamLength(data, packetStart, out var lengthField))
        {
            return data;
        }

        var edits = new List<(int start, int end, byte[] replacement)>
        {
            (packetStart, contentEnd, canonical),
            (lengthField.start, lengthField.end, AsciiDigits(canonical.Length))
        };
        edits.Sort((left, right) => left.start.CompareTo(right.start));

        // startxref points at the cross-reference keyword, which trails both edits, so it is repointed to
        // the shifted position. This third edit sits after the table and so does not move it.
        var all = new List<(int start, int end, byte[] replacement)>(edits)
        {
            (startxref.start, startxref.end, AsciiDigits(Shift(edits, xrefKeyword)))
        };

        var rebuilt = ApplyEdits(data, all);

        // Repair each in-use entry with the shifted offset of its object. The entry field and the object
        // it points at are both original positions, mapped through the same shift.
        foreach (var entry in entries)
        {
            if (entry.inUse)
            {
                WriteOffset(rebuilt, Shift(edits, entry.fieldOffset), Shift(edits, entry.objectOffset));
            }
        }

        return rebuilt;
    }

    // Drops every run of whitespace that sits between a '>' and a '<' (ignorable inter-element whitespace
    // and packet padding). Whitespace inside text content or attribute values is preserved.
    static byte[] CollapseInterTagWhitespace(byte[] data, int start, int end)
    {
        var output = new List<byte>(end - start);
        var index = start;
        while (index < end)
        {
            var current = data[index];
            if (IsWhitespace(current) && output.Count > 0 && output[^1] == (byte) '>')
            {
                var runEnd = index;
                while (runEnd < end && IsWhitespace(data[runEnd]))
                {
                    runEnd++;
                }

                if (runEnd < end && data[runEnd] == (byte) '<')
                {
                    index = runEnd;
                    continue;
                }
            }

            output.Add(current);
            index++;
        }

        return output.ToArray();
    }

    // Reads the sole classic cross-reference table: the keyword position, every entry (the object offset
    // it records and where that offset field lives), and the digit span of the sole startxref value.
    // Returns false for anything else (a cross-reference stream, an incremental update, or a malformed
    // table) so the caller can leave the document untouched.
    static bool TryReadClassicXref(
        byte[] data,
        out int xrefKeyword,
        out List<(int objectOffset, int fieldOffset, bool inUse)> entries,
        out (int start, int end) startxref)
    {
        xrefKeyword = -1;
        entries = [];
        startxref = default;

        // A second startxref implies an incremental update, which this does not rewrite.
        var startxrefKeyword = IndexOf(data, "startxref"u8, 0);
        if (startxrefKeyword < 0 ||
            IndexOf(data, "startxref"u8, startxrefKeyword + 1) >= 0)
        {
            return false;
        }

        if (!TryFindXrefTable(data, out xrefKeyword))
        {
            return false;
        }

        var position = SkipEol(data, xrefKeyword + 4);

        // Cross-reference subsections until the trailer keyword.
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

                var offset = ParseFixedInt(data, position, 10);
                var type = data[position + 17];
                entries.Add((offset, position, type == (byte) 'n'));
                position += 20;
            }
        }

        var digits = SkipWhitespace(data, startxrefKeyword + "startxref"u8.Length);
        var digitsEnd = digits;
        while (digitsEnd < data.Length && IsDigit(data[digitsEnd]))
        {
            digitsEnd++;
        }

        if (digitsEnd == digits)
        {
            return false;
        }

        startxref = (digits, digitsEnd);
        return true;
    }

    // Finds the cross-reference table keyword: an "xref" that begins a line (which excludes the "xref"
    // inside "startxref"). Fails when there is not exactly one, so cross-reference streams and
    // incremental updates are rejected.
    static bool TryFindXrefTable(byte[] data, out int position)
    {
        position = -1;
        var search = 0;
        while (true)
        {
            var hit = IndexOf(data, "xref"u8, search);
            if (hit < 0)
            {
                return position >= 0;
            }

            search = hit + 4;
            if (hit == 0 || data[hit - 1] != (byte) '\r' && data[hit - 1] != (byte) '\n')
            {
                continue;
            }

            if (position >= 0)
            {
                position = -1;
                return false;
            }

            position = hit;
        }
    }

    // Locates the metadata stream length value, either the direct "/Length n" in the dictionary or, for
    // the indirect "/Length g 0 R" form, the numeric value of object g.
    static bool TryFindStreamLength(byte[] data, int packetStart, out (int start, int end) lengthField)
    {
        lengthField = default;

        // The metadata dictionary sits immediately before the packet, so its /Length is the nearest one.
        var key = LastIndexOf(data, "/Length"u8, packetStart);
        if (key < 0)
        {
            return false;
        }

        var digitsStart = SkipWhitespace(data, key + "/Length"u8.Length);
        var cursor = digitsStart;
        if (!TryReadInt(data, ref cursor, out var first))
        {
            return false;
        }

        // Indirect form "/Length g 0 R": the value lives in object g.
        var probe = SkipWhitespace(data, cursor);
        if (TryReadInt(data, ref probe, out var generation))
        {
            probe = SkipWhitespace(data, probe);
            if (probe < data.Length && data[probe] == (byte) 'R')
            {
                return TryFindIndirectLength(data, first, generation, out lengthField);
            }
        }

        // Direct form "/Length n".
        lengthField = (digitsStart, cursor);
        return true;
    }

    // Finds "objectNumber generation obj" at the start of a line and returns the span of the integer that
    // follows (the stream length held in an indirect object).
    static bool TryFindIndirectLength(byte[] data, int objectNumber, int generation, out (int start, int end) lengthField)
    {
        lengthField = default;

        var header = AsciiBytes($"{objectNumber} {generation} obj");
        var search = 0;
        while (true)
        {
            var hit = IndexOf(data, header, search);
            if (hit < 0)
            {
                return false;
            }

            search = hit + header.Length;
            if (hit != 0 && data[hit - 1] != (byte) '\r' && data[hit - 1] != (byte) '\n')
            {
                continue;
            }

            var digitsStart = SkipWhitespace(data, hit + header.Length);
            var cursor = digitsStart;
            if (!TryReadInt(data, ref cursor, out _))
            {
                return false;
            }

            lengthField = (digitsStart, cursor);
            return true;
        }
    }

    // Builds a new buffer with each edit (sorted, non-overlapping) spliced in.
    static byte[] ApplyEdits(byte[] data, List<(int start, int end, byte[] replacement)> edits)
    {
        var length = data.Length;
        foreach (var edit in edits)
        {
            length += edit.replacement.Length - (edit.end - edit.start);
        }

        var output = new byte[length];
        var read = 0;
        var write = 0;
        foreach (var edit in edits)
        {
            var copy = edit.start - read;
            Array.Copy(data, read, output, write, copy);
            write += copy;
            Array.Copy(edit.replacement, 0, output, write, edit.replacement.Length);
            write += edit.replacement.Length;
            read = edit.end;
        }

        Array.Copy(data, read, output, write, data.Length - read);
        return output;
    }

    // Overwrites a cross-reference entry's fixed 10-digit, zero-padded offset field.
    static void WriteOffset(byte[] data, int position, int offset)
    {
        for (var index = 9; index >= 0; index--)
        {
            data[position + index] = (byte) ('0' + offset % 10);
            offset /= 10;
        }
    }

    static int IndexOf(byte[] data, ReadOnlySpan<byte> value, int start)
    {
        var hit = data.AsSpan(start).IndexOf(value);
        return hit < 0 ? -1 : hit + start;
    }

    static int LastIndexOf(byte[] data, ReadOnlySpan<byte> value, int end) =>
        data.AsSpan(0, end).LastIndexOf(value);

    static bool StartsWith(byte[] data, int position, ReadOnlySpan<byte> value) =>
        position + value.Length <= data.Length &&
        data.AsSpan(position, value.Length).SequenceEqual(value);

    static bool IsAllWhitespace(byte[] data, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (!IsWhitespace(data[index]))
            {
                return false;
            }
        }

        return true;
    }

    static int SkipEol(byte[] data, int position)
    {
        if (position < data.Length && data[position] == (byte) '\r')
        {
            position++;
        }

        if (position < data.Length && data[position] == (byte) '\n')
        {
            position++;
        }

        return position;
    }

    static bool TryReadInt(byte[] data, ref int position, out int value)
    {
        value = 0;
        var start = position;
        while (position < data.Length && IsDigit(data[position]))
        {
            value = value * 10 + (data[position] - '0');
            position++;
        }

        return position > start;
    }

    static int ParseFixedInt(byte[] data, int position, int width)
    {
        var value = 0;
        for (var index = 0; index < width; index++)
        {
            value = value * 10 + (data[position + index] - '0');
        }

        return value;
    }

    static byte[] AsciiDigits(int value)
    {
        if (value == 0)
        {
            return [(byte) '0'];
        }

        var length = 0;
        for (var remaining = value; remaining > 0; remaining /= 10)
        {
            length++;
        }

        var bytes = new byte[length];
        for (var index = length - 1; index >= 0; index--)
        {
            bytes[index] = (byte) ('0' + value % 10);
            value /= 10;
        }

        return bytes;
    }

    static byte[] AsciiBytes(string value)
    {
        var bytes = new byte[value.Length];
        for (var index = 0; index < value.Length; index++)
        {
            bytes[index] = (byte) value[index];
        }

        return bytes;
    }
}
