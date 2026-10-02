// Reading one file: CSV/TSV with Sep, workbooks with ExcelDataReader.
//
// Both stream rows into one ColumnProfiler per column, so memory is bounded by
// what the profilers keep, not by the size of the file.

using System.Globalization;
using ExcelDataReader;
using nietras.SeparatedValues;

namespace DepGraph;

/// <summary>One sheet, read and profiled. <see cref="Columns"/> is empty when the sheet had no header.</summary>
internal sealed record Sheet(string Name, long Rows, List<ColumnProfiler> Columns);

internal static class Readers
{
    // ExcelDataReader looks up Windows-1252 as its fallback encoding, even for .xlsx.
    static Readers() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    public static List<Sheet> Read(Stream stream, string extension, string stem, Profile profile, int? maxRows, int sampleN) =>
        extension switch
        {
            ".csv" => [Csv(stream, ',', stem, profile, maxRows, sampleN)],
            ".tsv" => [Csv(stream, '\t', stem, profile, maxRows, sampleN)],
            _ => Workbook(stream, profile, maxRows, sampleN),
        };

    static Sheet Csv(Stream stream, char separator, string stem, Profile profile, int? maxRows, int sampleN)
    {
        using var text = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        using var reader = Sep.New(separator)
            .Reader(o => o with { HasHeader = false, DisableColCountCheck = true, Unescape = true })
            .From(text);

        List<ColumnProfiler>? columns = null;
        long rows = 0;
        foreach (var row in reader)
        {
            if (row.ColCount == 1 && row[0].Span.IsEmpty)
                continue; // a blank line
            if (columns is null)
            {
                var header = new string?[row.ColCount];
                for (var i = 0; i < header.Length; i++)
                    header[i] = row[i].ToString();
                columns = Profilers(header, profile, sampleN);
                continue;
            }
            if (rows == maxRows)
                break;
            rows++;
            // A ragged line: extra fields are dropped, missing ones are empty.
            var n = Math.Min(row.ColCount, columns.Count);
            for (var i = 0; i < n; i++)
                columns[i].AddText(row[i].Span, parse: true);
            for (var i = n; i < columns.Count; i++)
                columns[i].AddNull();
        }
        return new Sheet(stem, rows, columns ?? []);
    }

    static List<Sheet> Workbook(Stream stream, Profile profile, int? maxRows, int sampleN)
    {
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var sheets = new List<Sheet>();
        do
        {
            // The header is the first row with anything in it: a sheet need not start at A1.
            var width = reader.FieldCount;
            var header = new string?[width];
            var found = false;
            while (!found && reader.Read())
            {
                for (var i = 0; i < width; i++)
                    found |= (header[i] = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)) is { Length: > 0 };
            }
            if (!found)
            {
                sheets.Add(new Sheet(reader.Name, 0, []));
                continue;
            }
            var columns = Profilers(header, profile, sampleN);

            long rows = 0, blank = 0;
            var values = new object?[width];
            while (reader.Read() && rows != maxRows)
            {
                var any = false;
                for (var i = 0; i < width; i++)
                    any |= (values[i] = reader.GetValue(i)) is not null;
                // A workbook can carry formatted but empty rows below the data.
                // They count only once a row with values follows them.
                if (!any)
                {
                    blank++;
                    continue;
                }
                for (; blank > 0 && rows != maxRows; blank--, rows++)
                {
                    foreach (var c in columns)
                        c.AddNull();
                }
                if (rows == maxRows)
                    break;
                rows++;
                for (var i = 0; i < width; i++)
                    columns[i].AddValue(values[i]);
            }

            // Columns beside the data with no header and no values are formatting, not data.
            while (columns.Count > 0 && string.IsNullOrEmpty(header[columns.Count - 1]) && columns[^1].NonNull == 0)
                columns.RemoveAt(columns.Count - 1);
            var lead = 0;
            while (lead < columns.Count && string.IsNullOrEmpty(header[lead]) && columns[lead].NonNull == 0)
                lead++;
            columns.RemoveRange(0, lead);
            sheets.Add(new Sheet(reader.Name, rows, columns));
        } while (reader.NextResult());
        return sheets;
    }

    /// <summary>One profiler per header cell; a blank header is named by position, a repeated one is numbered.</summary>
    static List<ColumnProfiler> Profilers(string?[] header, Profile profile, int sampleN)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var output = new List<ColumnProfiler>(header.Length);
        for (var i = 0; i < header.Length; i++)
        {
            var name = string.IsNullOrEmpty(header[i]) ? $"__UNNAMED__{i}" : header[i]!;
            if (seen.TryGetValue(name, out var n))
            {
                seen[name] = n + 1;
                name = $"{name}_duplicated_{n}";
            }
            else
            {
                seen[name] = 0;
            }
            output.Add(new ColumnProfiler(name, profile, sampleN));
        }
        return output;
    }
}
