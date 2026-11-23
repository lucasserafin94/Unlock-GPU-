using System.Globalization;
using System.Text;

namespace UnlockGpu.App.Services;

/// <summary>
/// Converts a session CSV (invariant: ';' separator, '.' decimals, '#' metadata line) into a file Excel opens directly
/// with Brazilian regional settings: ';' separator, ',' decimals, UTF-8 with BOM, no metadata line.
/// </summary>
public static class SessionCsvExporter
{
    /// <summary>Field separator of both formats.</summary>
    public const char Separator = ';';

    /// <summary>Writes the converted copy of <paramref name="sourceCsv"/> to <paramref name="targetCsv"/>.</summary>
    /// <returns>Number of data rows written (header excluded).</returns>
    public static int Export(string sourceCsv, string targetCsv)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceCsv);
        ArgumentException.ThrowIfNullOrEmpty(targetCsv);
        int rows = -1;
        // The recorder keeps the file open with FileShare.ReadWrite while recording.
        using var input = new FileStream(sourceCsv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string tmp = targetCsv + ".tmp";
        using (var writer = new StreamWriter(tmp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                writer.WriteLine(ConvertLine(line));
                rows++;
            }
        }
        File.Move(tmp, targetCsv, overwrite: true);
        return Math.Max(rows, 0);
    }

    /// <summary>Converts one line: every field that is an invariant decimal number gets ',' as decimal separator.</summary>
    public static string ConvertLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string[] fields = line.Split(Separator);
        for (int i = 0; i < fields.Length; i++)
        {
            string f = fields[i];
            if (f.Contains('.', StringComparison.Ordinal)
                && double.TryParse(f, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
                fields[i] = f.Replace('.', ',');
        }
        return string.Join(Separator, fields);
    }
}
