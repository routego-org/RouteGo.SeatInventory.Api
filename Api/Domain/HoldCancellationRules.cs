namespace Api.Domain;

/// <summary>What an admin must give to cancel a hold: a note of 10 to 500 characters saying why.</summary>
public static class HoldCancellationRules
{
    /// <summary>The shortest note an admin may give.</summary>
    public const int MinNoteLength = 10;

    /// <summary>The longest note an admin may give.</summary>
    public const int MaxNoteLength = 500;

    /// <summary>
    /// True for a note of 10 to 500 characters that says something. A note of spaces only is
    /// refused even when long enough, because the audit log would then carry no reason at all.
    /// </summary>
    public static bool IsValidNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return false;
        }

        return note.Length >= MinNoteLength && note.Length <= MaxNoteLength;
    }
}
