using System.Text;

namespace WorkHub.Core.Workspace.Search;

/// <summary>
/// Search text rules (Phase 6 proposal §4.2–§4.4). Matching uses the Workspace name comparison: NFKC, then invariant upper
/// case (<see cref="WorkspaceName.Key"/>), as a contains test. An item matches when every term is contained in at least
/// one of its searched fields.
/// </summary>
public static class WorkspaceSearchText
{
    public const int MaxQueryLength = 200;
    public const int MaxTerms = 8;
    public const int ExcerptLength = 120;

    /// <summary>Null for an empty query (the tree is shown). Never throws for any input.</summary>
    public static WorkspaceSearchQuery? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var shortened = false;
        var input = text;
        if (input.Length > MaxQueryLength)
        {
            input = input[..MaxQueryLength];
            if (char.IsHighSurrogate(input[^1]))
                input = input[..^1]; // never keep half of a pair cut by the length cap
            shortened = true;
        }

        var terms = new List<string>();
        foreach (var raw in SplitOnWhitespace(RemoveLoneSurrogates(input)))
        {
            var term = Fold(raw);
            if (term.Length == 0 || terms.Contains(term, StringComparer.Ordinal))
                continue;
            if (terms.Count == MaxTerms)
            {
                shortened = true;
                break;
            }

            terms.Add(term);
        }

        return terms.Count == 0 ? null : new WorkspaceSearchQuery(terms) { WasShortened = shortened };
    }

    /// <summary>The comparison form of any stored text (lone surrogates are dropped so normalization cannot throw).</summary>
    public static string Fold(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : WorkspaceName.Key(RemoveLoneSurrogates(text));

    /// <summary>Bit i is set when term i is contained in the already folded text (at most 8 terms, so a byte-sized mask).</summary>
    public static int MaskOfFolded(string folded, IReadOnlyList<string> terms)
    {
        var mask = 0;
        for (var i = 0; i < terms.Count; i++)
        {
            if (folded.Contains(terms[i], StringComparison.Ordinal))
                mask |= 1 << i;
        }

        return mask;
    }

    public static int Mask(string? text, IReadOnlyList<string> terms) =>
        string.IsNullOrEmpty(text) ? 0 : MaskOfFolded(Fold(text), terms);

    public static int AllTerms(IReadOnlyList<string> terms) => (1 << terms.Count) - 1;

    /// <summary>
    /// The first line of <paramref name="text"/> whose folded form contains a term, shortened to about
    /// <see cref="ExcerptLength"/> characters around the first match, tabs as spaces. Null when no line matches.
    /// Offsets are found on the original line (NFKC can change lengths), so no character is cut in half.
    /// </summary>
    public static string? Excerpt(string? text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Replace('\t', ' ');
            var folded = Fold(line);
            var term = terms.FirstOrDefault(t => folded.Contains(t, StringComparison.Ordinal));
            if (term is null)
                continue;

            line = line.Trim();
            if (line.Length <= ExcerptLength)
                return line;

            var end = FirstPrefixContaining(line, term);
            var start = Math.Max(0, end - ExcerptLength / 2);
            var length = Math.Min(ExcerptLength, line.Length - start);
            start = Math.Max(0, Math.Min(start, line.Length - length));
            (start, length) = SurrogateSafe(line, start, length);
            var excerpt = line.Substring(start, length).Trim();
            return (start > 0 ? "…" : string.Empty) + excerpt + (start + length < line.Length ? "…" : string.Empty);
        }

        return null;
    }

    /// <summary>Display order: name matches first, then kind rank, then the tree's name order, then kind, then Id.</summary>
    public static int Compare(WorkspaceSearchHit x, bool xNameMatch, WorkspaceSearchHit y, bool yNameMatch)
    {
        var byNameMatch = yNameMatch.CompareTo(xNameMatch);
        if (byNameMatch != 0)
            return byNameMatch;
        var byKind = x.Item.Kind.CompareTo(y.Item.Kind);
        if (byKind != 0)
            return byKind;
        var byName = WorkspaceNodeOrder.Instance.Compare((WorkspaceNodeType.Folder, x.Name, 0), (WorkspaceNodeType.Folder, y.Name, 0));
        return byName != 0 ? byName : x.Item.Id.CompareTo(y.Item.Id);
    }

    /// <summary>The joined display path used for locations ("Clients › Alpha Portal › Specs").</summary>
    public static string JoinPath(IEnumerable<string> parts) => string.Join(" › ", parts);

    private static IEnumerable<string> SplitOnWhitespace(string text)
    {
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) // includes U+3000 IDEOGRAPHIC SPACE
            {
                if (current.Length > 0)
                    yield return current.ToString();
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static string RemoveLoneSurrogates(string text)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                builder ??= new StringBuilder(text, 0, i, text.Length);
                continue;
            }

            builder?.Append(c);
        }

        return builder?.ToString() ?? text;
    }

    /// <summary>Smallest original prefix length whose fold contains <paramref name="term"/> (binary search; the line matches).</summary>
    private static int FirstPrefixContaining(string line, string term)
    {
        int low = 1, high = line.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            var cut = char.IsHighSurrogate(line[mid - 1]) && mid < line.Length ? mid + 1 : mid;
            if (Fold(line[..cut]).Contains(term, StringComparison.Ordinal))
                high = mid;
            else
                low = mid + 1;
        }

        return low;
    }

    private static (int Start, int Length) SurrogateSafe(string line, int start, int length)
    {
        if (start > 0 && char.IsLowSurrogate(line[start]))
        {
            start++;
            length--;
        }

        var end = start + length;
        if (end < line.Length && end > 0 && char.IsHighSurrogate(line[end - 1]))
            length--;
        return (start, length);
    }
}
