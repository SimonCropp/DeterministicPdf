# Claude Code Reference

This document contains important information about the codebase for future reference.

## What this library does

`PdfNormalizer` neutralizes the fields of a PDF that change on every render, so the same source
document always produces byte-identical output. It operates on the raw bytes — no PDF parser, no
third-party PDF dependency.

## PDF structure notes

### Volatile fields

Two places record the same volatile information, and both must be handled:

1. **The document information dictionary** (classic, in the trailer):
   - `/CreationDate (D:20240115093000+05'30')`
   - `/ModDate (D:20240115093000Z)`
2. **The XMP metadata packet** (XML, in a `/Type /Metadata` stream):
   - `xmp:CreateDate`, `xmp:ModifyDate`, `xmp:MetadataDate` — direct text content
   - `dc:date` — an *ordered array* (`seq Date`) per the XMP spec, so the value is nested in
     `rdf:Seq`/`rdf:li`, not direct text content. This is why `ZeroXmpElementTree` exists alongside
     `ZeroXmpElement`; the latter alone steps straight over it.
   - `xmpMM:DocumentID`, `xmpMM:InstanceID`, `xmpMM:OriginalDocumentID`
   - `stEvt:when`, `stEvt:instanceID` — the volatile fields of a `ResourceEvent`, one per entry in the
     `xmpMM:History` array. A producer that records a save event appends one with a fresh timestamp on
     every render. The siblings that say *what* happened rather than *when* (`stEvt:action`,
     `stEvt:softwareAgent`) are deliberately left alone.
   - `stRef:instanceID`, `stRef:documentID`, `stRef:originalDocumentID`, `stRef:lastModifyDate` — the
     same idea for the `ResourceRef` of `xmpMM:DerivedFrom`.

   All of these except `dc:date` have a *second* serialization. The compact RDF form carries a simple
   property as an attribute of the enclosing `rdf:Description` or `rdf:li`
   (`xmp:CreateDate="2024-01-15T09:30:00Z"`) rather than as an element, and that is what iText emits.
   `ZeroXmpElement` matches on `<` followed by the name, so it never sees that form — hence
   `ZeroXmpAttribute`, which requires the name to be preceded by whitespace (XML demands it before an
   attribute name, and it is also what rejects the element form and a longer name ending with this
   one) and followed by `=`. `dc:date` has no attribute pass because an ordered array cannot be
   written that way — it is the only XMP pass that is not a `ZeroXmpProperty`.

   `ZeroXmpProperty` is the pairing of the two: every property is declared once, with its `<openTag`,
   and the attribute name is derived as `openTag[1..]` so the two forms cannot drift apart. A document
   uses one serialization or the other, so at most one half of each pair finds anything, and both
   report under the same name.

   `CollapseInterTagWhitespace` only drops whitespace runs that sit between `>` and `<`, so the
   newlines *inside* a multi-attribute `rdf:Description` tag survive canonicalization untouched.

Plus the trailer file identifier `/ID [<...> <...>]`.

A third, per-producer stamp is `/LastModified` in a page or page-piece (`/PieceInfo`) dictionary. It is
a plain date string like `/ModDate`, so the same `ZeroPdfString` pass covers it. PDFTron writes one
onto the form XObject it uses for a watermark:
`/PieceInfo<</PDFTRON<</LastModified(D:20260729134217Z)/Private/Watermark>>>>`. Note the value can
follow the key with no separating whitespace.

### Why values are zeroed rather than removed

Zeroing is **length-preserving**, so every offset in the cross-reference table stays valid and no
repair is needed. `Fill.Digits` keeps separators (so a date stays readable), `Fill.Hex` zeroes hex
digits, `Fill.All` zeroes every non-whitespace byte.

This is also why look-alike keys have to be rejected explicitly — `/IDTree` is a name-tree key, not
the file identifier, and `NextXmpElementContent` rejects both longer element names sharing a prefix
and self-closing tags.

### XMP whitespace canonicalization (the hard part)

Apache FOP serializes the XMP packet through the platform's XML writer, so the JDK decides the
indentation: one machine emits a compact packet, another indents every element. Once the values are
zeroed that whitespace is the only remaining cross-platform difference, so `CanonicalizeXmp`
collapses inter-tag whitespace.

That **changes the packet length**, which invalidates offsets. So three things are repaired:

1. The metadata stream `/Length` — either the direct `/Length n` form or the indirect
   `/Length g 0 R` form (where the value lives in object `g`).
2. Every in-use cross-reference table entry's fixed 10-digit offset field.
3. The `startxref` value.

`Shift(position)` maps an original byte position to its post-edit position; both the entry field and
the object it points at are original positions run through the same map.

Canonicalization **bails out and returns the input unchanged** for any shape it cannot safely
rewrite: no packet, more than one packet, a cross-reference *stream* (rather than a table), an
incremental update (a second `startxref`), or an unlocatable stream length. The zeroing passes still
apply in that case, since they are length-preserving and always safe.

The pass is idempotent: if the content is already canonical the bytes (and the xref table) are left
untouched.

### What is deliberately not handled

- **Compressed values.** A value inside an `/ObjStm` object stream or a flate-compressed XMP packet
  does not appear literally in the bytes, so it is left as-is.
- **Encrypted documents.** Encrypted PDFs seed the encryption key from the trailer `/ID`; zeroing it
  would leave the document undecryptable.

## API shape

- `Normalize(byte[])` returns a normalized **copy** — the caller's array is never modified. Internally
  `NormalizeCore` does the work in place, so the stream overloads (which own the buffer they just
  built) skip the defensive copy.
- `Normalize(Stream)` / `NormalizeAsync(Stream, Cancel)` return a fresh `MemoryStream` at position 0.

Normalizing is not a streaming operation — the xref table at the tail records positions of objects at
the head — so the whole document is always materialized in a buffer.

## The change report

`Normalize(byte[], out IReadOnlyList<NormalizeChange>)` and `Normalize(Stream, out ...)` report what
was neutralized. `NormalizeCore` always builds a `ChangeRecorder`; the non-reporting overloads just
drop it.

The rule that makes the report worth anything: **a pass records only when bytes actually differed**,
never merely because it ran. `Overwrite` is the single choke point every zeroing pass goes through,
so it returns whether it changed anything, and it skips writing `'0'` over a `'0'`. Without that, a
second normalization of the same document would report every field again and the report could not be
used to answer "why is this document not deterministic?".

The canonicalization pass is reported by reference equality instead: every bail-out path in
`CanonicalizeXmp`, and the already-canonical case, returns the input array itself, and only a real
rewrite returns a new one.

Names come from the `u8` key spans the passes already carry, so there is no parallel list of strings
to drift — `ChangeRecorder.AsciiString` decodes them by hand rather than through `Encoding.ASCII`,
whose span overloads differ across the six target frameworks. XMP names are recorded as
`openTag[1..]`, dropping the `<` so the report names the element rather than its opening tag.

There is deliberately no async reporting overload; the reason is on the comment in
`PdfNormalizer_Streams.cs`.

## Testing

- TUnit (`await Assert.That(x).IsEqualTo(y)`), no Verify — these are plain assertion tests.
- `dotnet test` requires the Microsoft.Testing.Platform opt-in in `global.json`:
  ```json
  "test": { "runner": "Microsoft.Testing.Platform" }
  ```
  Without it the .NET 10 SDK routes through VSTest and fails.
- `Docnet.Core` is a **test-only** dependency. It re-loads the normalized output to prove the
  document is still valid — this is what actually catches a broken xref repair. The library itself
  has no PDF-reading dependency.
- Sample documents (copied from Verify.DocNet):
  - `sample.pdf` — a plain two-page document
  - `sample-fop.pdf` — an uncompressed FOP-style XMP packet with `dc:date` in `rdf:Seq`/`rdf:li`
  - `sample-fop-compact.pdf` / `sample-fop-indented.pdf` — the same document as serialized by two
    different JREs; they must normalize to identical bytes
  - `sample-xmp-attributes.pdf` — a hand-built one-page document whose XMP packet uses the compact
    RDF serialization: every property is an attribute of `rdf:Description`, as iText writes it.
    Unlike the others it does not come from Verify.DocNet. It is deliberately *not* named
    "compact" — `sample-fop-compact.pdf` is named for its packet indentation, which is a different
    axis entirely. The packet is indented and padded, so the document also exercises canonicalization
    and the xref repair on top of the attribute passes.

## Project structure

- `src/DeterministicPdf/` — the library
  - `PdfNormalizer.cs` — the byte[] entry point and every pass
  - `PdfNormalizer_Streams.cs` — stream overloads
- `src/Tests/` — TUnit tests
