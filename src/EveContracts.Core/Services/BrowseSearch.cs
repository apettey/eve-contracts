namespace EveContracts.Core.Services;

/// <summary>
/// All Contracts search syntax. Commas separate terms; every term must match.
/// A term matches when ALL of its words appear in one single field — one item
/// name, the title, or a location — so "raven, ballistic control" means "has a
/// Raven AND has a Ballistic Control item", not five loose words anywhere.
/// </summary>
public static class BrowseSearch
{
    /// <summary>Parse into lowercase terms, each a list of words. Empty input → no terms (match all).</summary>
    public static string[][] Parse(string? search) =>
        (search ?? "").ToLowerInvariant()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length > 0)
            .ToArray();

    /// <summary>True when one term's words all occur in the (lowercase) field.</summary>
    public static bool TermMatches(string[] term, string field)
    {
        foreach (var w in term)
            if (!field.Contains(w, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>True when the lowercase field satisfies any term (used to highlight items).</summary>
    public static bool AnyTermMatches(string[][] terms, string field)
    {
        foreach (var t in terms)
            if (TermMatches(t, field)) return true;
        return false;
    }

    /// <summary>True when every term is satisfied by at least one of the lowercase fields.</summary>
    public static bool Matches(string[][] terms, IReadOnlyList<string> fields)
    {
        foreach (var t in terms)
        {
            var hit = false;
            for (var i = 0; i < fields.Count && !hit; i++) hit = TermMatches(t, fields[i]);
            if (!hit) return false;
        }
        return true;
    }
}
