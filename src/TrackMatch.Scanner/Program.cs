using System.Globalization;
using TrackMatch.Infrastructure.Audio;
using TrackMatch.Infrastructure.Scanning;

return Run(args);

static int Run(string[] args)
{
    if (args.Length != 2 || !string.Equals(args[0], "scan", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Usage: TrackMatch.Scanner scan <folder>");
        return 1;
    }

    var scanner = new FlacLibraryScanner(new FlacMetadataReader());
    var total = 0;
    var succeeded = 0;
    var failed = 0;

    try
    {
        foreach (var result in scanner.Scan(args[1]))
        {
            total++;
            if (!result.IsSuccess)
            {
                failed++;
                Console.Error.WriteLine($"ERROR\t{result.Path}\t{result.ErrorMessage}");
                continue;
            }

            succeeded++;
            var metadata = result.Metadata!;
            Console.WriteLine(string.Join(
                '\t',
                metadata.Path,
                metadata.Duration.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture),
                string.Join("; ", metadata.Artists),
                metadata.Title ?? string.Empty,
                metadata.Album ?? string.Empty,
                metadata.TrackNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                metadata.DiscNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                string.Join("; ", metadata.Genres)));
        }
    }
    catch (DirectoryNotFoundException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }

    Console.Error.WriteLine($"Scanned: {total}, Success: {succeeded}, Failed: {failed}");
    return failed == 0 ? 0 : 2;
}
