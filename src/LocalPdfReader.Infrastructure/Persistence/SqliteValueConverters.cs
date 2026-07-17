using System.Globalization;

namespace LocalPdfReader.Infrastructure.Persistence;

internal static class SqliteValueConverters
{
    public static string FormatDateTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseDateTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static int FormatBoolean(bool value) => value ? 1 : 0;

    public static bool ParseBoolean(long value) => value != 0;
}
