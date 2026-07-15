using System.Globalization;

namespace CleverPoint.Migrator.Core.Model;

/// <summary>
/// Date parsing/formatting for the browser UI. Kept deliberately separate from display strings: a
/// grid must sort by the real instant, never by a locale-formatted string. A string like "12/06/2026"
/// sorts by its leading number, so on a dd/MM locale the Modified column appears to sort by day-of-month
/// (the bug this exists to prevent). Parse to a DateTime, sort by that, and only format for display.
/// </summary>
public static class DateText
{
    /// <summary>
    /// Parses an ISO-8601 date (as returned by the REST items endpoint, and by RenderListDataAsStream
    /// when DatesInUtc is set) to a real instant. Invariant + RoundtripKind so the "Z"/offset is honoured
    /// and parsing never depends on the current thread culture. Returns null for anything unparseable.
    /// </summary>
    public static DateTime? TryParseIso(string? value) =>
        !string.IsNullOrEmpty(value)
        && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt : null;

    /// <summary>Friendly display of an instant in the viewer's local time ("g" = short date + short time).</summary>
    public static string FormatLocal(DateTime? dt) => dt.HasValue ? dt.Value.ToLocalTime().ToString("g") : "";

    /// <summary>
    /// Formats a (site-local) date for ValidateUpdateListItem's string FieldValue, in the TARGET WEB'S
    /// locale. ValidateUpdateListItem parses the string using the web's regional settings, so the string
    /// must be written in that same locale -- a fixed US "M/d/yyyy" is read as dd/MM on a French/UK web,
    /// swapping day and month (verified live: "3/5" stored as 3 May) or erroring for day &gt; 12. ISO-8601 is
    /// NOT a safe substitute here: ValidateUpdateListItem rejects it ("valid date within range"). Using the
    /// web culture's own general short pattern ("g") is exactly what that locale round-trips.
    /// </summary>
    public static string ForFormUpdate(DateTime siteLocal, CultureInfo webCulture) =>
        siteLocal.ToString("g", webCulture);

    /// <summary>The CultureInfo for a SharePoint web LocaleId (LCID), falling back to invariant if unmappable.</summary>
    public static CultureInfo CultureForLcid(int lcid)
    {
        try { return CultureInfo.GetCultureInfo(lcid); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }
}
