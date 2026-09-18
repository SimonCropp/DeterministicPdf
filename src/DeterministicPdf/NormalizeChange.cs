namespace DeterministicPdf;

/// <summary>
/// One kind of volatile content that <see cref="PdfNormalizer"/> altered, and how many occurrences of
/// it were altered.
/// </summary>
/// <param name="Name">
/// What was neutralized, named as it appears in the document: a key such as <c>/CreationDate</c> or
/// <c>/ID</c>, an XMP property such as <c>xmp:CreateDate</c> (under that name whether the producer
/// serialized it as an element or as an attribute of <c>rdf:Description</c>), or
/// <c>XMP packet whitespace</c> for the canonicalization pass.
/// </param>
/// <param name="Count">How many occurrences were altered.</param>
public record NormalizeChange(string Name, int Count);
