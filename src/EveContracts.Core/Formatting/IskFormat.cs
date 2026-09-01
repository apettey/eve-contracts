using System.Globalization;

namespace EveContracts.Core.Formatting;

public static class IskFormat
{
    /// <summary>1.45B / 385.0M / 22K style, minus sign U+2212 per the design spec.</summary>
    public static string Short(double n)
    {
        var abs = Math.Abs(n);
        var sign = n < 0 ? "−" : "";
        if (abs >= 1e9) return sign + (abs / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + "B";
        if (abs >= 1e6) return sign + (abs / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        if (abs >= 1e3) return sign + (abs / 1e3).ToString("0", CultureInfo.InvariantCulture) + "K";
        return sign + abs.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>Profit format: prefixed + when non-negative.</summary>
    public static string Signed(double n) => (n >= 0 ? "+" : "") + Short(n);

    public static string Expiry(DateTime expiresUtc, DateTime nowUtc)
    {
        var span = expiresUtc - nowUtc;
        if (span <= TimeSpan.Zero) return "expired";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d {span.Hours}h";
    }
}
