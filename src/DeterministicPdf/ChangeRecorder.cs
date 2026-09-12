namespace DeterministicPdf;

// Accumulates what the passes actually altered, in the order they run.
//
// A pass records only when Overwrite reports that bytes really differed, never merely because the
// pass ran. That distinction is the whole value of the report: normalizing an already normalized
// document reports nothing, so the list doubles as the reason a document is not yet deterministic.
class ChangeRecorder
{
    List<NormalizeChange> changes = [];

    public IReadOnlyList<NormalizeChange> Changes => changes;

    public void Record(ReadOnlySpan<byte> name) => Record(AsciiString(name));

    public void Record(string name)
    {
        for (var index = 0; index < changes.Count; index++)
        {
            if (changes[index].Name == name)
            {
                changes[index] = changes[index] with
                {
                    Count = changes[index].Count + 1
                };
                return;
            }
        }

        changes.Add(new(name, 1));
    }

    // Deliberately not Encoding.ASCII: every caller passes a compile time ASCII literal, and the
    // span taking overloads of Encoding differ across the target frameworks.
    static string AsciiString(ReadOnlySpan<byte> value)
    {
        var characters = new char[value.Length];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = (char) value[index];
        }

        return new(characters);
    }
}
