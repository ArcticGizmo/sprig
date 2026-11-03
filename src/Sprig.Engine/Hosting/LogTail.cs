using System.Text;

namespace Sprig.Engine.Hosting;

/// <summary>
/// Reads the lines added to a runner's log since the last look. The host keeps the file open for writing, so it's
/// opened shared. Only whole lines are taken: a line still being written, or a character split across two reads, is
/// picked up next time. A runner started afresh recreates its log, which shows as the file getting shorter, so reading
/// starts again from the top.
/// </summary>
public sealed class LogTail(string file)
{
    static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    long _position;

    public string File => file;

    /// <summary>Goes up each time the log was found started afresh, so a reader can drop what it showed before.</summary>
    public int Generation { get; private set; }

    public IReadOnlyList<string> ReadNew()
    {
        if (!System.IO.File.Exists(file)) return [];
        byte[] bytes;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position)
            {
                _position = 0;
                Generation++;
            }
            if (stream.Length == _position) return [];
            stream.Position = _position;
            bytes = new byte[stream.Length - _position];
            stream.ReadExactly(bytes);
        }
        catch (IOException) { return []; }

        var end = Array.LastIndexOf(bytes, (byte)'\n');
        if (end < 0) return [];
        var start = _position == 0 && bytes.AsSpan().StartsWith(Bom) ? Bom.Length : 0;
        _position += end + 1;
        if (end < start) return [];

        var text = Encoding.UTF8.GetString(bytes, start, end - start);
        return text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    }
}
