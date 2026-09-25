using System.Text;

namespace AmigaSharp.Runtime.Dos;

/// <summary>
/// Runs AmigaDOS commands for dos Execute and SystemTagList. The runtime cannot run the command files in C:, so it
/// has its own versions of the common commands: Copy, Delete, List, Dir, Assign, MakeDir, Rename, Echo, Type and CD.
/// </summary>
public sealed class Shell(Core core)
{
    /// <summary>The return code of a command that the shell does not have.</summary>
    public const int UnknownCommand = -1;

    // The return codes of AmigaDOS commands.
    public const int Ok = 0;
    public const int Error = 10;
    public const int Failure = 20;

    private FileSystem Files => core.FileSystem;

    /// <summary>Runs a command line. Returns the return code of the command, or <see cref="UnknownCommand"/>.</summary>
    /// <param name="output">The output of the command if the command line does not redirect it.</param>
    public int Run(string commandLine, uint currentDirectory, Stream output)
    {
        var words = Split(commandLine);
        if (words.Count == 0)
            return Ok;

        // ">file", ">>file" and "<file" redirect the output and the input. The name can also be the next word.
        Stream? redirected = null;
        var arguments = new List<string>();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (word.StartsWith('>') || word.StartsWith('<'))
            {
                var append = word.StartsWith(">>");
                var name = word.TrimStart('>', '<');
                if (name.Length == 0 && i + 1 < words.Count)
                    name = words[++i];
                if (word.StartsWith('>'))
                    redirected = OpenOutput(name, append, currentDirectory);
                continue;
            }

            arguments.Add(word);
        }

        var command = arguments[0];
        command = command[(command.LastIndexOfAny([':', '/']) + 1)..].ToLowerInvariant();
        arguments.RemoveAt(0);
        var target = redirected ?? output;
        try
        {
            var writer = new StreamWriter(target, Encoding.Latin1, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            return command switch
            {
                "copy" => Copy(arguments, currentDirectory, writer),
                "delete" => Delete(arguments, currentDirectory, writer),
                "list" or "dir" => List(arguments, currentDirectory, writer, command == "dir"),
                "assign" => Assign(arguments, writer),
                "makedir" => MakeDir(arguments, currentDirectory, writer),
                "rename" => Rename(arguments, currentDirectory, writer),
                "echo" => Echo(arguments, writer),
                "type" => TypeFile(arguments, currentDirectory, target, writer),
                "cd" => Ok,
                "mount" => Fail(writer, $"Mount: the runtime has no device {string.Join(' ', arguments)}"),
                _ => UnknownCommand,
            };
        }
        finally
        {
            redirected?.Dispose();
        }
    }

    private int Copy(List<string> arguments, uint directory, StreamWriter output)
    {
        var all = RemoveKeyword(arguments, "ALL");
        RemoveKeyword(arguments, "CLONE");
        RemoveKeyword(arguments, "QUIET");
        RemoveKeyword(arguments, "FROM");
        RemoveKeyword(arguments, "TO");
        if (arguments.Count < 2)
            return Fail(output, "Copy: the source or the destination is missing");

        var (destination, destinationError) = Files.Resolve(arguments[^1], directory, mustExist: false);
        if (destination == null)
            return Fail(output, $"Copy: {arguments[^1]}: error {destinationError}");

        var sources = arguments.Take(arguments.Count - 1).SelectMany(source => Expand(source, directory)).ToList();
        if (sources.Count == 0)
            return Fail(output, $"Copy: no object matches {string.Join(' ', arguments.Take(arguments.Count - 1))}");

        var toDirectory = Directory.Exists(destination) || sources.Count > 1;
        foreach (var source in sources)
        {
            var target = toDirectory ? Path.Combine(destination, Path.GetFileName(source)) : destination;
            if (Directory.Exists(source))
            {
                if (all)
                    CopyDirectory(source, toDirectory && sources.Count == 1 && !arguments[0].Contains('#') ? destination : target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            output.WriteLine($"{Path.GetFileName(source)}..copied");
        }

        return Ok;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var child in Directory.GetDirectories(source))
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
    }

    private int Delete(List<string> arguments, uint directory, StreamWriter output)
    {
        var all = RemoveKeyword(arguments, "ALL");
        RemoveKeyword(arguments, "QUIET");
        RemoveKeyword(arguments, "FORCE");
        var result = Ok;
        foreach (var argument in arguments)
        {
            // "DIR/" deletes the objects in the directory.
            var name = argument.EndsWith('/') ? argument + "#?" : argument;
            var matches = Expand(name, directory).ToList();
            if (matches.Count == 0)
            {
                output.WriteLine($"{argument}: object not found");
                result = Error;
                continue;
            }

            foreach (var match in matches)
            {
                if (Directory.Exists(match))
                {
                    if (all || !Directory.EnumerateFileSystemEntries(match).Any())
                        Directory.Delete(match, recursive: true);
                    else
                        result = Error;
                }
                else
                {
                    File.Delete(match);
                }

                output.WriteLine($"{Path.GetFileName(match)}  Deleted");
            }
        }

        return result;
    }

    private int List(List<string> arguments, uint directory, StreamWriter output, bool isDir)
    {
        var quick = RemoveKeyword(arguments, "QUICK") || isDir;
        var noHead = RemoveKeyword(arguments, "NOHEAD") || isDir;
        var name = arguments.FirstOrDefault() ?? "";
        var (hostPath, error) = Files.Resolve(name, directory, mustExist: true);
        IEnumerable<string> entries;
        if (hostPath != null && Directory.Exists(hostPath))
            entries = Directory.EnumerateFileSystemEntries(hostPath).Order(StringComparer.OrdinalIgnoreCase);
        else if (AmigaPattern.HasWildcards(name))
            entries = Expand(name, directory);
        else if (hostPath != null)
            entries = [hostPath];
        else
            return Fail(output, $"List: {name}: object not found (error {error})");

        if (!noHead)
            output.WriteLine($"Directory \"{name}\"");
        foreach (var entry in entries)
        {
            var entryName = Path.GetFileName(entry);
            if (quick)
                output.WriteLine(entryName);
            else if (Directory.Exists(entry))
                output.WriteLine($"{entryName,-24} Dir");
            else
                output.WriteLine($"{entryName,-24} {new FileInfo(entry).Length,8}");
        }

        return Ok;
    }

    private int Assign(List<string> arguments, StreamWriter output)
    {
        if (arguments.Count == 0)
            return Ok;
        var name = arguments[0].TrimEnd(':');
        if (arguments.Count == 1 || arguments[1].Equals("DISMOUNT", StringComparison.OrdinalIgnoreCase)
                                 || arguments[1].Equals("REMOVE", StringComparison.OrdinalIgnoreCase))
        {
            Files.RemoveVolume(name);
            return Ok;
        }

        // The target must exist, as for the Assign command.
        var (target, _) = Files.Resolve(arguments[1], 0, mustExist: true);
        if (target == null)
            return Fail(output, $"Assign: {arguments[1]}: object not found");
        Files.AddAssign(name, arguments[1]);
        return Ok;
    }

    private int MakeDir(List<string> arguments, uint directory, StreamWriter output)
    {
        foreach (var argument in arguments)
        {
            var (path, error) = Files.Resolve(argument, directory, mustExist: false);
            if (path == null)
                return Fail(output, $"MakeDir: {argument}: error {error}");
            Directory.CreateDirectory(path);
        }

        return Ok;
    }

    private int Rename(List<string> arguments, uint directory, StreamWriter output)
    {
        RemoveKeyword(arguments, "FROM");
        RemoveKeyword(arguments, "TO");
        if (arguments.Count != 2)
            return Fail(output, "Rename: two names are necessary");
        var (source, _) = Files.Resolve(arguments[0], directory, mustExist: true);
        var (target, _) = Files.Resolve(arguments[1], directory, mustExist: false);
        if (source == null || target == null)
            return Fail(output, $"Rename: {arguments[0]}: object not found");
        if (Directory.Exists(target))
            target = Path.Combine(target, Path.GetFileName(source));
        if (Directory.Exists(source))
            Directory.Move(source, target);
        else
            File.Move(source, target, overwrite: true);
        return Ok;
    }

    private static int Echo(List<string> arguments, StreamWriter output)
    {
        var noLine = RemoveKeyword(arguments, "NOLINE");
        output.Write(string.Join(' ', arguments));
        if (!noLine)
            output.WriteLine();
        return Ok;
    }

    private int TypeFile(List<string> arguments, uint directory, Stream target, StreamWriter output)
    {
        foreach (var argument in arguments)
        {
            var (path, _) = Files.Resolve(argument, directory, mustExist: true);
            if (path == null || !File.Exists(path))
                return Fail(output, $"Type: {argument}: object not found");
            target.Write(File.ReadAllBytes(path));
        }

        return Ok;
    }

    /// <summary>The host paths of the objects that a name or a pattern selects. A pattern can be in the last part only.</summary>
    private IEnumerable<string> Expand(string name, uint directory)
    {
        var split = Math.Max(name.LastIndexOf('/'), name.LastIndexOf(':'));
        var last = name[(split + 1)..];
        if (!AmigaPattern.HasWildcards(last))
        {
            var (path, _) = Files.Resolve(name, directory, mustExist: true);
            return path == null ? [] : [path];
        }

        var (parent, _) = Files.Resolve(name[..(split + 1)], directory, mustExist: true);
        if (parent == null || !Directory.Exists(parent))
            return [];
        var pattern = AmigaPattern.ToRegex(last);
        return Directory.EnumerateFileSystemEntries(parent)
            .Where(entry => pattern.IsMatch(Path.GetFileName(entry)))
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    private Stream OpenOutput(string name, bool append, uint directory)
    {
        if (name.Equals("NIL:", StringComparison.OrdinalIgnoreCase))
            return Stream.Null;
        if (name == "*" || name.StartsWith("CON:", StringComparison.OrdinalIgnoreCase))
            return new NonClosingStream(core.Output);
        var (path, _) = Files.Resolve(name, directory, mustExist: false);
        if (path == null)
            return Stream.Null;
        return new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    }

    private static bool RemoveKeyword(List<string> arguments, string keyword) =>
        arguments.RemoveAll(argument => argument.Equals(keyword, StringComparison.OrdinalIgnoreCase)) > 0;

    private static int Fail(StreamWriter output, string message)
    {
        output.WriteLine(message);
        return Failure;
    }

    /// <summary>Splits a command line into words. Double quotes keep spaces in a word.</summary>
    private static List<string> Split(string commandLine)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        var inWord = false;
        foreach (var c in commandLine.TrimEnd('\n', '\r'))
        {
            if (c == '"')
            {
                quoted = !quoted;
                inWord = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (inWord)
                    words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }

        if (inWord)
            words.Add(word.ToString());
        return words;
    }

    /// <summary>A stream that does not close the stream under it.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
