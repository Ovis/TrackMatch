using System.Globalization;
using System.Text;
using TrackMatch.Core.Probe;

internal static class ProbeAnalysisCsv
{
    private static readonly string[] RequiredColumns =
    [
        "ExpectedRelation",
        "Similarity",
        "CoverageA",
        "CoverageB",
        "DurationRatio",
    ];

    public static IReadOnlyList<ProbeMeasurement> ReadMeasurements(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("Probe結果CSVが空である。");
        }

        var header = ParseLine(lines[0]);
        var indexes = RequiredColumns.ToDictionary(
            name => name,
            name => FindColumn(header, name),
            StringComparer.OrdinalIgnoreCase);

        var measurements = new List<ProbeMeasurement>();
        for (var lineNumber = 2; lineNumber <= lines.Length; lineNumber++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineNumber - 1]))
            {
                continue;
            }

            var fields = ParseLine(lines[lineNumber - 1]);
            try
            {
                measurements.Add(new ProbeMeasurement(
                    GetRequired(fields, indexes["ExpectedRelation"]),
                    GetNumber(fields, indexes["Similarity"]),
                    GetNumber(fields, indexes["CoverageA"]),
                    GetNumber(fields, indexes["CoverageB"]),
                    GetNumber(fields, indexes["DurationRatio"])));
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException($"Probe結果CSVの{lineNumber}行目が不正である: {exception.Message}", exception);
            }
        }

        if (measurements.Count == 0)
        {
            throw new InvalidDataException("Probe結果CSVに測定値が存在しない。");
        }

        return measurements;
    }

    private static int FindColumn(IReadOnlyList<string> header, string name)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new InvalidDataException($"必須列 {name} が見つからない。");
    }

    private static string GetRequired(IReadOnlyList<string> fields, int index)
    {
        if (index >= fields.Count || string.IsNullOrWhiteSpace(fields[index]))
        {
            throw new InvalidDataException("必須項目が空である。");
        }

        return fields[index];
    }

    private static double GetNumber(IReadOnlyList<string> fields, int index)
    {
        var value = GetRequired(fields, index);
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number))
        {
            throw new InvalidDataException($"数値として解釈できない: {value}");
        }

        return number;
    }

    private static IReadOnlyList<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (quoted)
            {
                if (character == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(character);
                }
            }
            else if (character == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else if (character == '"' && current.Length == 0)
            {
                quoted = true;
            }
            else
            {
                current.Append(character);
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("引用符が閉じられていない。");
        }

        fields.Add(current.ToString());
        return fields;
    }
}
