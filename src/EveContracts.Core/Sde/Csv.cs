namespace EveContracts.Core.Sde;

/// <summary>
/// Minimal streaming CSV reader for SDE dumps. Handles quoted fields containing
/// commas, quotes, and embedded newlines (item descriptions have all three).
/// </summary>
public static class Csv
{
    public static IEnumerable<string[]> ReadRecords(TextReader reader)
    {
        var field = new System.Text.StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        int ch;
        var any = false;

        while ((ch = reader.Read()) != -1)
        {
            var c = (char)ch;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { record.Add(field.ToString()); field.Clear(); any = true; }
            else if (c == '\r') { /* swallow */ }
            else if (c == '\n')
            {
                if (any || field.Length > 0)
                {
                    record.Add(field.ToString());
                    yield return record.ToArray();
                }
                field.Clear(); record.Clear(); any = false;
            }
            else field.Append(c);
        }
        if (any || field.Length > 0)
        {
            record.Add(field.ToString());
            yield return record.ToArray();
        }
    }
}
