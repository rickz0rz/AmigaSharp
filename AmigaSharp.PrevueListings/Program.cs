using System.Globalization;
using System.Net.Sockets;
using AmigaSharp.PrevueListings;
using AmigaSharp.PrevueListings.Logos;

const string usage = """
    Usage: AmigaSharp.PrevueListings --server <url> --output <directory> [options]
           AmigaSharp.PrevueListings logos --input <directory> --output <directory>

    Reads the guide of a Channels DVR server and writes the Prevue listing files curday.dat and nxtday.dat. The files
    have the HD channels of the guide, in the order of their numbers, and a maximum of 200 channels.

    Options:
      --server <url>        The address of the Channels DVR server, for example http://channels-dvr.local:8089.
      --output <dir>        The directory for curday.dat and nxtday.dat: the drive of Prevue.
      --date <date>         The broadcast day, for example 2026-09-26. The default is the current day of ESQ: it
                            changes at 5:30 AM.
      --max-channels <n>    The maximum number of channels. The default and the limit of ESQ is 200.
      --premium <list>      The premium channels: channel numbers or call signs, with commas between them, for example
                            "222,HBOHD". The grid shows their programs on a red background. The option can occur
                            more than once.
      --insecure            Accept any HTTPS certificate of the server.
      --feed <file>         Also write the listings as a Prevue serial data feed, for the --serial-file option of the
                            launcher.
      --selection <code>    The selection code in the address command of the feed. The default is "*", which all
                            machines accept.
      --serve <host:port>   After the files, stay running: connect to the serial bridge of the launcher, and send the
                            changes of the guide as a feed. ESQ then updates the grid while it runs.
      --interval <minutes>  The time between two reads of the guide with --serve. The default is 10.
      --ready <file>        Make this file when curday.dat and nxtday.dat are written. A script can wait for it.
      --channel-logos       Also make the channel logos of the drive from the logo images of the channels of
                            Channels DVR (see the logos command). A cache directory in the drive keeps the images.
      --logos <dir>         Also make channel logos from the PNG files of the directory. They replace the images of
                            --channel-logos with the same names.
      --clock <date>        The time of the Amiga at the start, for example 2026-09-27T04:50, as the --date option of
                            the launcher. The tool then chooses the broadcast days by the time of the Amiga, not by the
                            time of the host.

    The logos command makes the channel logos of the drive from the PNG files of a directory, without a server. The
    name of a file is the call letters of its channel, for example KTIVDT.png. The logos go to Logos/Channels in the
    drive, and their lines to LOGO.LST. ESQ shows a channel logo with the call letters and the channel number.
    """;

if (args.Length > 0 && args[0] == "logos")
    return MakeLogos(args[1..]);

Uri? server = null;
string? output = null;
DateOnly? fixedDate = null;
var insecure = false;
string? feedPath = null;
var selection = "*";
var maxChannels = PrevueDataFile.MaximumChannels;
var premium = new List<string>();
string? serve = null;
var interval = TimeSpan.FromMinutes(10);
string? readyPath = null;
var clockOffset = TimeSpan.Zero;
var channelLogos = false;
string? logoDirectory = null;
try
{
    for (var i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
        switch (args[i])
        {
            case "--server": server = new Uri(Next().TrimEnd('/') + "/"); break;
            case "--output": output = Next(); break;
            case "--date": fixedDate = DateOnly.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--insecure": insecure = true; break;
            case "--feed": feedPath = Next(); break;
            case "--max-channels": maxChannels = Math.Clamp(int.Parse(Next()), 1, PrevueDataFile.MaximumChannels); break;
            case "--selection": selection = Next(); break;
            case "--premium": premium.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
            case "--serve": serve = Next(); break;
            case "--interval": interval = TimeSpan.FromMinutes(double.Parse(Next(), CultureInfo.InvariantCulture)); break;
            case "--ready": readyPath = Next(); break;
            case "--channel-logos": channelLogos = true; break;
            case "--logos": logoDirectory = Next(); break;
            case "--clock": clockOffset = DateTime.Parse(Next(), CultureInfo.InvariantCulture) - DateTime.Now; break;
            case "--help" or "-h":
                Console.WriteLine(usage);
                return 0;
            default:
                throw new ArgumentException($"unknown argument {args[i]}.");
        }
    }

    if (server == null || output == null)
        throw new ArgumentException("--server and --output are necessary.");
}
catch (Exception e) when (e is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    Console.Error.WriteLine(usage);
    return 2;
}

var zone = TimeZoneInfo.Local;
using var client = new ChannelsDvrClient(server, insecure);

var days = await ReadDaysAsync();
Directory.CreateDirectory(output);
File.WriteAllBytes(Path.Combine(output, "curday.dat"), PrevueDataFile.WriteCurrentDay(days[0]));
File.WriteAllBytes(Path.Combine(output, "nxtday.dat"), PrevueDataFile.WriteNextDay(days[1]));
Console.WriteLine($"Wrote curday.dat ({Describe(days[0])}) and nxtday.dat ({Describe(days[1])}) to {output}.");
if (feedPath != null)
{
    var feed = PrevueFeed.Listings(selection, days);
    File.WriteAllBytes(feedPath, feed);
    Console.WriteLine($"Wrote the feed to {feedPath} ({feed.Length} bytes, {feed.Length / 240.0 / 60:F1} minutes at 2400 baud).");
}

// The logos are ready before ESQ starts, because ESQ reads LOGO.LST when it starts.
if (channelLogos || logoDirectory != null)
{
    var logos = new Dictionary<string, byte[]>();
    if (channelLogos)
    {
        var channels = PrevueFeed.SourceNames(days[0].Channels.Take(PrevueDataFile.MaximumChannels));
        foreach (var (name, png) in await ChannelLogos.DownloadAsync(channels, Path.Combine(output, ".logo-cache"), Console.Out))
            logos[name] = png;
    }

    if (logoDirectory != null)
    {
        foreach (var (name, png) in ChannelLogos.FromDirectory(logoDirectory))
            logos[name] = png;
    }

    var count = ChannelLogos.Write(output, logos.Select(pair => (pair.Key, pair.Value)), Console.Out);
    Console.WriteLine($"Wrote {count} channel logos to {Path.Combine(output, ChannelLogos.Folder)}.");
}

if (readyPath != null)
    File.WriteAllText(readyPath, "");
if (serve == null)
    return 0;

// Serve the changes. ESQ has the listings of the files now, so they are the listings that it knows.
var known = days;
var (host, port) = ParseEndpoint(serve);
while (true)
{
    try
    {
        using var connection = await ConnectAsync(host, port);
        Console.WriteLine($"Connected to the serial bridge at {serve}.");
        await using var stream = connection.GetStream();
        while (true)
        {
            await Task.Delay(interval);
            var now = await ReadDaysAsync();
            var changes = PrevueFeed.Changes(selection, known, now);
            known = now;
            if (changes.Length == 0)
            {
                Console.WriteLine($"{DateTime.Now + clockOffset:HH:mm}: no changes.");
                continue;
            }

            await stream.WriteAsync(changes);
            Console.WriteLine($"{DateTime.Now + clockOffset:HH:mm}: sent {changes.Length} bytes of changes.");
        }
    }
    catch (Exception e) when (e is IOException or SocketException or HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm}: {e.Message} Trying again in 10 seconds.");
        await Task.Delay(TimeSpan.FromSeconds(10));
    }
}

async Task<PrevueDay[]> ReadDaysAsync()
{
    var date = fixedDate ?? GuideConverter.CurrentDay(DateTime.Now + clockOffset);
    var start = GuideConverter.StartOf(date, zone);
    Console.WriteLine($"Reading the guide from {server} for the broadcast days of {date:yyyy-MM-dd} and the day after.");
    var guide = await client.GetGuideAsync(start.AddHours(-6), TimeSpan.FromHours(54));
    return
    [
        GuideConverter.Convert(guide, date, zone, maxChannels, premium),
        GuideConverter.Convert(guide, date.AddDays(1), zone, maxChannels, premium),
    ];
}

static async Task<TcpClient> ConnectAsync(string host, int port)
{
    while (true)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port);
            return client;
        }
        catch (SocketException)
        {
            client.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}

static (string Host, int Port) ParseEndpoint(string text)
{
    var colon = text.LastIndexOf(':');
    return colon > 0 ? (text[..colon], int.Parse(text[(colon + 1)..])) : ("localhost", int.Parse(text));
}

static string Describe(PrevueDay day) => $"{day.Channels.Count} channels, {day.Channels.Sum(c => c.Programs.Count)} programs";

// The logos command: channel logos from the PNG files of a directory.
static int MakeLogos(string[] arguments)
{
    string? input = null, output = null;
    for (var i = 0; i < arguments.Length; i++)
    {
        switch (arguments[i])
        {
            case "--input" when i + 1 < arguments.Length: input = arguments[++i]; break;
            case "--output" when i + 1 < arguments.Length: output = arguments[++i]; break;
            default:
                Console.Error.WriteLine($"error: unknown argument {arguments[i]}.");
                return 2;
        }
    }

    if (input == null || output == null || !Directory.Exists(input))
    {
        Console.Error.WriteLine("error: logos needs --input <directory of PNG files> and --output <drive>.");
        return 2;
    }

    var count = ChannelLogos.Write(output, ChannelLogos.FromDirectory(input), Console.Out);
    Console.WriteLine($"Wrote {count} channel logos to {Path.Combine(output, ChannelLogos.Folder)}.");
    return 0;
}
