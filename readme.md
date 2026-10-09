# <img src="/src/icon.png" height="30px"> DeterministicPdf

[![Build status](https://github.com/SimonCropp/DeterministicPdf/actions/workflows/build.yml/badge.svg)](https://github.com/SimonCropp/DeterministicPdf/actions/workflows/build.yml)
[![NuGet Status](https://img.shields.io/nuget/v/DeterministicPdf.svg)](https://www.nuget.org/packages/DeterministicPdf/)

Modify PDF files to ensure they are deterministic. Helpful for testing, build reproducibility, security verification, and ensuring output integrity across different build environments.

A PDF records when it was produced and stamps every render with fresh identifiers, so the same source document never produces the same bytes twice. That defeats snapshot testing, content hashing, and reproducible builds. This neutralizes those fields.

**See [Milestones](../../milestones?state=closed) for release notes.**


## NuGet

 * https://nuget.org/packages/DeterministicPdf


## What is neutralized

 * The trailer file identifier `/ID [<...> <...>]`. A producer that writes it as a literal string (`/ID [(...) (...)]`, as Aspose.PDF does) has to escape whichever of its random bytes are not printable, so the same sixteen bytes take a different number of characters on every save. In the final trailer or cross-reference stream dictionary, where nothing with an offset follows it, such an identifier is also cut down to one `0` per byte it encoded.
 * The document information dictionary dates `/CreationDate` and `/ModDate`
 * The page and page-piece dictionary date `/LastModified`, which a producer stamps with a wall-clock time for its own private data (PDFTron writes one onto the form XObject it uses for a watermark)
 * The XMP metadata dates `xmp:CreateDate`, `xmp:ModifyDate`, and `xmp:MetadataDate`
 * The Dublin Core `dc:date`, whether written as direct text content or nested in an `rdf:Seq`/`rdf:li` array
 * The XMP per-generation identifiers `xmpMM:DocumentID`, `xmpMM:InstanceID`, and `xmpMM:OriginalDocumentID`
 * The volatile fields of the structs those identifiers are referenced from — `stEvt:when` and `stEvt:instanceID` in an `xmpMM:History` save event, and `stRef:instanceID`, `stRef:documentID`, `stRef:originalDocumentID`, and `stRef:lastModifyDate` in an `xmpMM:DerivedFrom` reference. A producer that records a save event stamps a fresh `stEvt:when` onto the history on every render. Fields that describe *what* happened rather than *when* (`stEvt:action`, `stEvt:softwareAgent`) are left alone.
 * The six letter tag that prefixes the name of a subset font (`/BaseFont /IIJUVL+OpenSans`, repeated as the `/FontName` of its descriptor). A producer that embeds only the glyphs a document uses picks this tag, and some (Aspose.PDF among them) pick it at random on every save. Each distinct tag is replaced, in order of first appearance, with `AAAAAA`, `AAAAAB`, and so on, so two subsets of one font stay distinct and the length is unchanged.

Every XMP property above is handled in both RDF serializations: as an element of its own (`<xmp:CreateDate>2024-01-15T09:30:00Z</xmp:CreateDate>`, which Apache FOP writes) and in the compact form that carries it as an attribute of the enclosing `rdf:Description` or `rdf:li` (`xmp:CreateDate="2024-01-15T09:30:00Z"`, which iText writes). `dc:date` is the one exception: an ordered array cannot be serialized as an attribute at all.

Neutralizing replaces the mutable characters of each value with `0` rather than removing it. Dates keep their separators (`D:00000000000000Z`) so the result stays readable and, more importantly, stays the same length: every cross-reference offset in the document remains valid.

A date's UTC offset is made of separators, so it survives the zeroing and goes on recording where the render happened. It is neutralized in two steps, because it varies in two ways. The sign — `+00:00` on a build agent east of Greenwich, `-00:00` on a developer machine west of it — is the same length either way, so it is forced to `+`, the spelling ISO 8601 gives a zero offset. Only a sign that follows the time is treated as one: the `-` separating the year, month and day of an ISO 8601 date is left as it is.


### Time zone designators of different lengths

The offset also varies in *length*: a producer writes `Z` on a machine running in UTC and `+10:30` anywhere else, so the two renders are different-sized documents and no amount of zeroing can reconcile them. Every designator is therefore collapsed to `Z`.

That shortens the document, so — exactly as for the XMP packet below — the metadata stream length, the cross-reference table offsets and `startxref` are repaired afterwards. A document that cannot be safely rewritten is left to the zeroing alone, which still forces the sign. A date inside a stream whose length this cannot restate is skipped rather than shortened out from under it.


## How it works

 * For an input document
 * Zero the volatile values in place, preserving the length of each
 * Replace the tags of subset font names with canonical ones, also in place
 * Collapse every date's UTC offset to `Z`, and repair the offsets that shifted
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


### Stripping embedded fonts

A producer embeds a subset of whichever copy of a font the machine has installed. A developer machine with Arial 7.06 and a build agent with Arial 7.01 therefore render the same document to different bytes: the two font programs draw the same glyphs, but they are different lengths, and being compressed they hold nothing that could be zeroed. Aspose.Words, Aspose.Cells and Aspose.PDF all behave this way.

Every `Normalize` overload has a counterpart taking `stripEmbeddedFonts`, which removes the font programs so the two renders agree:

<!-- snippet: StripEmbeddedFonts -->
<a id='snippet-StripEmbeddedFonts'></a>
```cs
var withoutFonts = PdfNormalizer.Normalize(bytes, stripEmbeddedFonts: true);
```
<sup><a href='/src/Tests/Snippets.cs#L40-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-StripEmbeddedFonts' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For each font descriptor entry `/FontFile`, `/FontFile2` or `/FontFile3`:

 * The entry is removed from the font descriptor, leaving the font named but not embedded
 * The data of the font stream is removed
 * Every length the stream declared (`/Length`, `/Length1`, `/Length2`, `/Length3`) is restated as `0`, in the dictionary or in the object an indirect one refers to
 * An object that holds nothing but the stream's length, and that nothing refers to, is restated as `0` as well (Aspose.PDF leaves one behind)

The stream usually sits in the middle of the document, so everything after it moves and the cross-reference section is repaired. A classic table has its offsets rewritten in place. A cross-reference stream is rewritten uncompressed, since recompressing it would make the result depend on the deflate implementation of the runtime doing the work.

This is lossy, which is why it is off by default: a viewer opening the result substitutes fonts of its own. It suits a snapshot that is compared rather than read. Each removed entry is reported under its key (`/FontFile2`).

Fonts are left embedded when the document cannot be safely rewritten: an incremental update, a hybrid cross-reference table (`/XRefStm`), a cross-reference stream that uses a predictor (`/DecodeParms`) or a filter other than `/FlateDecode`, or a font descriptor held in an `/ObjStm` object stream.
