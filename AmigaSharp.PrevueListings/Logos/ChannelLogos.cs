using System.Security.Cryptography;
using System.Text;

namespace AmigaSharp.PrevueListings.Logos;

/// <summary>
/// Writes the channel logos into the drive of Prevue: the pictures in Logos/Channels, and their lines in LOGO.LST.
/// </summary>
/// <remarks>
/// A line of LOGO.LST without a comma is a channel logo: ESQ shows it only if its file name is the source name of a
/// channel of the listings, and it writes the call letters and the channel number on it. The logos of this class are
/// the only files of Logos/Channels, and the only lines of LOGO.LST in that folder, so each run replaces them. The
/// other lines of LOGO.LST stay.
/// </remarks>
public static class ChannelLogos
{
    public const string Folder = "Logos/Channels";
    private const string ListFile = "LOGO.LST";

    /// <summary>
    /// The name of a logo for a channel name or a file name: the letters and digits, a maximum of 6, in upper case, as
    /// the source names of the listings (see <see cref="PrevueFeed.SourceNames"/>).
    /// </summary>
    public static string Name(string text) =>
        new string(text.Where(char.IsAsciiLetterOrDigit).Take(6).ToArray()).ToUpperInvariant();

    /// <summary>
    /// Replaces the channel logos of the drive: it makes each picture from its image, writes it to Logos/Channels, and
    /// writes the lines of LOGO.LST. An image that cannot be read is left out.
    /// </summary>
    /// <param name="logos">The source names and the PNG files of their images.</param>
    /// <returns>The number of logos that the drive has now.</returns>
    public static int Write(string drive, IEnumerable<(string Name, byte[] Png)> logos, TextWriter log)
    {
        var folder = Path.Combine(drive, Folder);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var names = new List<string>();
        foreach (var (name, png) in logos)
        {
            if (name.Length == 0 || names.Contains(name))
                continue;
            try
            {
                File.WriteAllBytes(Path.Combine(folder, name), ChannelLogo.Create(PngImage.Read(png)));
                names.Add(name);
            }
            catch (InvalidDataException e)
            {
                log.WriteLine($"The logo of {name} is left out: {e.Message}");
            }
        }

        WriteList(drive, names);
        return names.Count;
    }

    /// <summary>Writes the lines of the channel logos in LOGO.LST, after its other lines.</summary>
    private static void WriteList(string drive, IReadOnlyList<string> names)
    {
        var path = Path.Combine(drive, ListFile);
        var lines = File.Exists(path)
            ? File.ReadAllText(path, Encoding.Latin1).Split('\n').Select(line => line.TrimEnd('\r'))
                .Where(line => line.Trim().Length > 0 &&
                               !line.StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];
        lines.AddRange(names.Select(name => $"{Folder}/{name}"));
        File.WriteAllText(path, string.Concat(lines.Select(line => line + "\r\n")), Encoding.Latin1);
    }

    /// <summary>
    /// The PNG files of a directory, by the names of their logos: the file name without ".png", for example
    /// KTIVDT.png is the logo of KTIVDT.
    /// </summary>
    public static IEnumerable<(string Name, byte[] Png)> FromDirectory(string directory) =>
        Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(file => (Name(Path.GetFileNameWithoutExtension(file)), File.ReadAllBytes(file)));

    /// <summary>
    /// Downloads the logo images of the channels, for their source names. A cache directory keeps each image, so the
    /// next run does not download it again. An image that does not download is left out.
    /// </summary>
    public static async Task<List<(string Name, byte[] Png)>> DownloadAsync(
        IEnumerable<(string Source, PrevueChannel Channel)> channels, string cache, TextWriter log)
    {
        Directory.CreateDirectory(cache);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var result = new List<(string, byte[])>();
        var failed = 0;
        foreach (var (source, channel) in channels)
        {
            if (channel.Image is not { } url)
                continue;
            var file = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16] + ".png");
            try
            {
                if (!File.Exists(file))
                    await File.WriteAllBytesAsync(file, await http.GetByteArrayAsync(url));
                result.Add((source, await File.ReadAllBytesAsync(file)));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                failed++;
            }
        }

        if (failed > 0)
            log.WriteLine($"{failed} logo images of Channels DVR did not download.");
        return result;
    }
}
