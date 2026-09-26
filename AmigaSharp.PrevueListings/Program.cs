using System.Globalization;
using AmigaSharp.PrevueListings;

const string usage = """
    Usage: AmigaSharp.PrevueListings --server <url> --output <directory> [options]

    Reads the guide of a Channels DVR server and writes the Prevue listing files curday.dat and nxtday.dat. The files
    have the HD channels of the guide, in the order of their numbers, and a maximum of 200 channels.

    Options:
      --server <url>      The address of the Channels DVR server, for example http://192.168.0.195:8089.
      --output <dir>      The directory for curday.dat and nxtday.dat: the drive of Prevue.
      --date <date>       The broadcast day, for example 2026-09-26. The default is the current broadcast day: the day
                          starts at 5:00 AM.
      --insecure          Accept any HTTPS certificate of the server.
    """;

Uri? server = null;
string? output = null;
DateOnly? date = null;
var insecure = false;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
    switch (args[i])
    {
        case "--server": server = new Uri(Next().TrimEnd('/') + "/"); break;
        case "--output": output = Next(); break;
        case "--date": date = DateOnly.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--insecure": insecure = true; break;
        case "--help" or "-h":
            Console.WriteLine(usage);
            return 0;
        default:
            Console.Error.WriteLine($"error: unknown argument {args[i]}.");
            Console.Error.WriteLine(usage);
            return 2;
    }
}

if (server == null || output == null)
{
    Console.Error.WriteLine("error: --server and --output are necessary.");
    Console.Error.WriteLine(usage);
    return 2;
}

var zone = TimeZoneInfo.Local;
// Before 5:00 AM, the current broadcast day is the day before.
var now = DateTime.Now;
date ??= DateOnly.FromDateTime(now.TimeOfDay < GuideConverter.DayStart.ToTimeSpan() ? now.AddDays(-1) : now);

using var client = new ChannelsDvrClient(server, insecure);
var start = GuideConverter.StartOf(date.Value, zone);
Console.WriteLine($"Reading the guide from {server} for the broadcast days of {date:yyyy-MM-dd} and the day after.");
var guide = await client.GetGuideAsync(start.AddHours(-6), TimeSpan.FromHours(54));

Directory.CreateDirectory(output);
var current = GuideConverter.Convert(guide, date.Value, zone);
var next = GuideConverter.Convert(guide, date.Value.AddDays(1), zone);
File.WriteAllBytes(Path.Combine(output, "curday.dat"), PrevueDataFile.WriteCurrentDay(current));
File.WriteAllBytes(Path.Combine(output, "nxtday.dat"), PrevueDataFile.WriteNextDay(next));
Console.WriteLine($"Wrote curday.dat ({current.Channels.Count} channels, {current.Channels.Sum(c => c.Programs.Count)} programs) " +
                  $"and nxtday.dat ({next.Channels.Count} channels, {next.Channels.Sum(c => c.Programs.Count)} programs) to {output}.");
return 0;
