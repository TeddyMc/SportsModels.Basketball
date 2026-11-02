using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace SportsModels.Data.NbaOfficial.Definitions;

internal static class SourceJson
{
    public static string Text(this JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString()
            : "";

    public static int Number(this JsonElement row, string name) =>
        int.TryParse(row.Text(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public static string Key(this JsonElement row, string name) => row.Text(name) is "0" or "" ? "" : row.Text(name);

    public static string Normalize(string value) => string.Concat(value.Normalize(System.Text.NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit)).ToLowerInvariant();

    public static double Remaining(string clock) => XmlConvert.ToTimeSpan(clock).TotalSeconds;

    public static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Fingerprint(JsonElement row) => Hash(System.Text.Encoding.UTF8.GetBytes(row.GetRawText()));

    public static double? Minutes(string value)
    {
        if (value.StartsWith("PT", StringComparison.Ordinal)) { return XmlConvert.ToTimeSpan(value).TotalMinutes; }
        var match = Regex.Match(value, @"^(\d+):(\d{2})(?:\.(\d+))?$");
        if (!match.Success) { return null; }
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            + double.Parse(match.Groups[2].Value + (match.Groups[3].Success ? "." + match.Groups[3].Value : ""), CultureInfo.InvariantCulture) / 60;
    }
}
