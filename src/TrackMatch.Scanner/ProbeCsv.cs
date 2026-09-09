using System.Globalization;
using System.Text;
using TrackMatch.Core.Probe;

internal static class ProbeCsv
{
    private static readonly string[] RequiredColumns = ["Label", "ExpectedRelation", "FileA", "FileB"];

    public static IReadOnlyList<ProbePair> ReadPairs(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("Probe入力CSVが空である。");
        }

        var header = ParseLine(lines[0]);
        var indexes = RequiredColumns.ToDictionary(
            name => name,
            name => FindColumn(header, name),
            StringComparer.OrdinalIgnoreCase);
        var notesIndex = FindOptionalColumn(header, "Notes");

        var pairs = new List<ProbePair>();
        for (var lineNumber = 2; lineNumber <= lines.Length; lineNumber++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineNumber - 1]))
            {
                continue;
            }

            var fields = ParseLine(lines[lineNumber - 1]);
            try
            {
                pairs.Add(new ProbePair(
                    GetRequired(fields, indexes["Label"]),
                    GetRequired(fields, indexes["ExpectedRelation"]),
                    GetRequired(fields, indexes["FileA"]),
                    GetRequired(fields, indexes["FileB"]),
                    notesIndex >= 0 && notesIndex < fields.Count ? NullIfEmpty(fields[notesIndex]) : null));
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException($"Probe入力CSVの{lineNumber}行目が不正である: {exception.Message}", exception);
            }
        }

        return pairs;
    }

    public static void WriteHeader(TextWriter writer)
    {
        writer.WriteLine("Label,ExpectedRelation,Notes,FileA,FileB,DurationASeconds,DurationBSeconds,FingerprintLengthA,FingerprintLengthB,BestOffsetSeconds,Similarity,MatchedDurationSeconds,CoverageA,CoverageB,DurationRatio");
    }

    public static void WriteResult(TextWriter writer, ProbeResult result)
    {
        writer.WriteLine(string.Join(",",
            Escape(result.Pair.Label),
            Escape(result.Pair.ExpectedRelation),
            Escape(result.Pair.Notes ?? string.Empty),
            Escape(result.FingerprintA.Path),
            Escape(result.FingerprintB.Path),
            Number(result.FingerprintA.Duration.TotalSeconds, "F3"),
            Number(result.FingerprintB.Duration.TotalSeconds, "F3"),
            result.FingerprintA.Values.Count.ToString(CultureInfo.InvariantCulture),
            result.FingerprintB.Values.Count.ToString(CultureInfo.InvariantCulture),
            Number(result.Comparison.BestOffset.TotalSeconds, "F3"),
            Number(result.Comparison.Similarity, "F6"),
            Number(result.Comparison.MatchedDuration.TotalSeconds, "F3"),
            Number(result.Comparison.CoverageA, "F6"),
            Number(result.Comparison.CoverageB, "F6"),
            Number(result.DurationRatio, "F6")));
    }

    private static int FindColumn(IReadOnlyList<string> header, string name)
    {
        var index = FindOptionalColumn(header, name);
        if (index < 0)
        {
            throw new InvalidDataException($"必須列 {name} が見つからない。");
        }

        return index;
    }

    private static int FindOptionalColumn(IReadOnlyList<string> header, string name)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string GetRequired(IReadOnlyList<string> fields, int index)
    {
        if (index >= fields.Count || string.IsNullOrWhiteSpace(fields[index]))
        {
            throw new InvalidDataException("必須項目が空である。");
        }

        return fields[index];
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

    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
