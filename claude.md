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

Plus the trailer file identifier `/ID [<...> <...>]`.

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

## Project structure

- `src/DeterministicPdf/` — the library
  - `PdfNormalizer.cs` — the byte[] entry point and every pass
  - `PdfNormalizer_Streams.cs` — stream overloads
- `src/Tests/` — TUnit tests
