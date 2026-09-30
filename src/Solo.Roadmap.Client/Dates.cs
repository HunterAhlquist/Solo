using System.Globalization;

namespace Solo.Roadmap.Client;

/// <summary>Short date text for bars, the picker and snackbars: "3 Oct", "3–10 Oct", "28 Sep – 4 Oct".</summary>
public static class Dates
{
    public static string Day(DateOnly day) => day.ToString("d MMM", CultureInfo.CurrentCulture);

    public static string Range(DateOnly start, DateOnly end)
    {
        if (start == end)
        {
            return Day(start);
        }

        if (start.Year != end.Year)
        {
            return $"{start.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} – " +
                   $"{end.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
        }

        return start.Month == end.Month
            ? $"{start.Day}–{Day(end)}"
            : $"{Day(start)} – {Day(end)}";
    }
}
