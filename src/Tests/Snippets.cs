public static class Snippets
{
    static async Task Usage(string pdfPath)
    {
        #region NormalizeBytes

        var bytes = File.ReadAllBytes(pdfPath);
        var normalized = PdfNormalizer.Normalize(bytes);

        #endregion

        #region NormalizeStream

        using var sourceStream = File.OpenRead(pdfPath);
        using var target = PdfNormalizer.Normalize(sourceStream);

        #endregion

        #region NormalizeStreamAsync

        using var asyncSource = File.OpenRead(pdfPath);
        using var asyncTarget = await PdfNormalizer.NormalizeAsync(asyncSource);

        #endregion
    }
}
