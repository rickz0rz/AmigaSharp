using System.Text;
using AmigaSharp.Runtime;
using AmigaSharp.Runtime.Exec;

namespace AmigaSharp.Tests.Exec;

public class ExecListTests
{
    private const uint List = 0x1000;

    private readonly Memory _memory = new();

    public ExecListTests()
    {
        ExecList.Initialize(_memory, List);
    }

    [Fact]
    public void Initialize_MakesAnEmptyList()
    {
        Assert.True(ExecList.IsEmpty(_memory, List));
        Assert.Equal(0u, ExecList.RemoveHead(_memory, List));
        Assert.Equal(0u, ExecList.RemoveTail(_memory, List));
    }

    [Fact]
    public void AddTailAndAddHead_KeepTheOrder()
    {
        ExecList.AddTail(_memory, List, Node(1));
        ExecList.AddTail(_memory, List, Node(2));
        ExecList.AddHead(_memory, List, Node(3));

        Assert.Equal([Node(3), Node(1), Node(2)], ExecList.Nodes(_memory, List));
        Assert.Equal(Node(2), ExecList.RemoveTail(_memory, List));
        Assert.Equal(Node(3), ExecList.RemoveHead(_memory, List));
        Assert.Equal([Node(1)], ExecList.Nodes(_memory, List));
    }

    [Fact]
    public void Enqueue_SortsByPriority_AndKeepsFifoForEqualPriority()
    {
        Enqueue(1, priority: 0);
        Enqueue(2, priority: 10);
        Enqueue(3, priority: 0);
        Enqueue(4, priority: -5);

        Assert.Equal([Node(2), Node(1), Node(3), Node(4)], ExecList.Nodes(_memory, List));
    }

    [Fact]
    public void FindName_FindsTheNode()
    {
        var name = 0x3000u;
        _memory.WriteBytes(name, Encoding.Latin1.GetBytes("second\0"));
        ExecList.AddTail(_memory, List, Node(1));
        ExecList.AddTail(_memory, List, Node(2));
        _memory.Write32(Node(2) + NodeOffsets.Name, name);

        Assert.Equal(Node(2), ExecList.FindName(_memory, List, "second"));
        Assert.Equal(0u, ExecList.FindName(_memory, List, "third"));
    }

    private void Enqueue(int number, sbyte priority)
    {
        _memory.Write8(Node(number) + NodeOffsets.Priority, (byte)priority);
        ExecList.Enqueue(_memory, List, Node(number));
    }

    private static uint Node(int number) => 0x2000 + (uint)number * 0x20;
}
