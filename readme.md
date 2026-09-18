# <img src="/src/icon.png" height="30px"> DeterministicPdf

[![Build status](https://img.shields.io/appveyor/build/SimonCropp/deterministicpdf)](https://ci.appveyor.com/project/SimonCropp/deterministicpdf)
[![NuGet Status](https://img.shields.io/nuget/v/DeterministicPdf.svg)](https://www.nuget.org/packages/DeterministicPdf/)

Modify PDF files to ensure they are deterministic. Helpful for testing, build reproducibility, security verification, and ensuring output integrity across different build environments.

A PDF records when it was produced and stamps every render with fresh identifiers, so the same source document never produces the same bytes twice. That defeats snapshot testing, content hashing, and reproducible builds. This neutralizes those fields.

**See [Milestones](../../milestones?state=closed) for release notes.**


## NuGet

 * https://nuget.org/packages/DeterministicPdf


## What is neutralized

 * The trailer file identifier `/ID [<...> <...>]`
 * The document information dictionary dates `/CreationDate` and `/ModDate`
 * The page and page-piece dictionary date `/LastModified`, which a producer stamps with a wall-clock time for its own private data (PDFTron writes one onto the form XObject it uses for a watermark)
 * The XMP metadata dates `xmp:CreateDate`, `xmp:ModifyDate`, and `xmp:MetadataDate`
 * The Dublin Core `dc:date`, whether written as direct text content or nested in an `rdf:Seq`/`rdf:li` array
 * The XMP per-generation identifiers `xmpMM:DocumentID`, `xmpMM:InstanceID`, and `xmpMM:OriginalDocumentID`
 * The volatile fields of the structs those identifiers are referenced from — `stEvt:when` and `stEvt:instanceID` in an `xmpMM:History` save event, and `stRef:instanceID`, `stRef:documentID`, `stRef:originalDocumentID`, and `stRef:lastModifyDate` in an `xmpMM:DerivedFrom` reference. A producer that records a save event stamps a fresh `stEvt:when` onto the history on every render. Fields that describe *what* happened rather than *when* (`stEvt:action`, `stEvt:softwareAgent`) are left alone.

Every XMP property above is handled in both RDF serializations: as an element of its own (`<xmp:CreateDate>2024-01-15T09:30:00Z</xmp:CreateDate>`, which Apache FOP writes) and in the compact form that carries it as an attribute of the enclosing `rdf:Description` or `rdf:li` (`xmp:CreateDate="2024-01-15T09:30:00Z"`, which iText writes). `dc:date` is the one exception: an ordered array cannot be serialized as an attribute at all.

Neutralizing replaces the mutable characters of each value with `0` rather than removing it. Dates keep their separators (`D:00000000000000Z`) so the result stays readable and, more importantly, stays the same length: every cross-reference offset in the document remains valid.


## How it works

 * For an input document
 * Zero the volatile values in place, preserving the length of each
 * Canonicalize the XMP metadata packet by collapsing inter-element whitespace
 * Repair the metadata stream `/Length`, the cross-reference table offsets, and `startxref` to match the new packet length


### XMP whitespace canonicalization

Apache FOP serializes the XMP packet through the platform's XML writer, so the same document is indented differently depending on which JRE produced it. Once the volatile values are zeroed, that whitespace is the only remaining cross-platform difference, so the packet is collapsed to a single canonical form.

Because this changes the packet length, the metadata stream length and the classic cross-reference table are repaired afterwards. A document that cannot be safely rewritten this way — a cross-reference stream, an incremental update, more than one XMP packet, or an unlocatable stream length — is left unchanged. The volatile values are still zeroed in that case, since that pass is length-preserving and always safe.


### Compressed values

A value that has been compressed away — inside an `/ObjStm` object stream, or a flate-compressed XMP packet — no longer appears literally in the bytes and is therefore left as-is.


### Encrypted documents

This targets unencrypted documents. Encrypted PDFs seed their encryption key from the trailer `/ID`; zeroing it would leave the document undecryptable, so encrypted input should not be passed here.


## Usage


### Normalize bytes

The input array is not modified; a normalized copy is returned.

<!-- snippet: NormalizeBytes -->
<a id='snippet-NormalizeBytes'></a>
```cs
var bytes = await File.ReadAllBytesAsync(pdfPath);
var normalized = PdfNormalizer.Normalize(bytes);
```
<sup><a href='/src/Tests/Snippets.cs#L9-L14' title='Snippet source file'>snippet source</a> | <a href='#snippet-NormalizeBytes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### Normalize a stream

Returns a fresh `MemoryStream` positioned at 0.

<!-- snippet: NormalizeStream -->
<a id='snippet-NormalizeStream'></a>
```cs
using var sourceStream = File.OpenRead(pdfPath);
using var target = PdfNormalizer.Normalize(sourceStream);
```
<sup><a href='/src/Tests/Snippets.cs#L16-L21' title='Snippet source file'>snippet source</a> | <a href='#snippet-NormalizeStream' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### NormalizeAsync

<!-- snippet: NormalizeStreamAsync -->
<a id='snippet-NormalizeStreamAsync'></a>
```cs
using var asyncSource = File.OpenRead(pdfPath);
using var asyncTarget = await PdfNormalizer.NormalizeAsync(asyncSource);
```
<sup><a href='/src/Tests/Snippets.cs#L23-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-NormalizeStreamAsync' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### Reporting what changed

An overload reports what was neutralized, named as it appears in the document: a key such as `/CreationDate` or `/ID`, an XMP property such as `xmp:CreateDate` (under the same name whichever serialization the producer used), or `XMP packet whitespace` for the canonicalization pass.

<!-- snippet: NormalizeReport -->
<a id='snippet-NormalizeReport'></a>
```cs
var reported = PdfNormalizer.Normalize(bytes, out var changes);
foreach (var change in changes)
{
    Console.WriteLine($"{change.Name} x{change.Count}");
}
```
<sup><a href='/src/Tests/Snippets.cs#L30-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-NormalizeReport' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A change is only reported when bytes actually differed, never merely because a pass ran. So an already normalized document reports nothing, and the list doubles as the answer to "why is this document not deterministic?".

There is no async counterpart. Only reading the stream is asynchronous — normalizing is synchronous work over the resident buffer — so an async overload would have to return the report beside the stream for no gain over reading the bytes first.
