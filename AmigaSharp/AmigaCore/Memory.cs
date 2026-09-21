namespace AmigaSharp.AmigaCore;

public class Memory
{
    private readonly List<byte> _data = new();
    private readonly Dictionary<string, int> _dataLocations = new();

    public byte this[int address] => _data[address];

    public void Append(string stringData, string name)
    {
        Append(stringData.ToCharArray().Select(c => (byte)c), name);
    }

    public void Append(IEnumerable<byte> byteData, string name)
    {
        _dataLocations.Add(name, _data.Count);
        _data.AddRange(byteData);
    }

    public int GetAddress(string name)
    {
        return _dataLocations[name];
    }
}
