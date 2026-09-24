using AmigaSharp.Generated;
using AmigaSharp.Runtime;

namespace AmigaSharp;

class Program
{
    static int Main(string[] args)
    {
        var executable = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "hello"));
        return (int)new HelloWorld(new Core()).Run(executable);
    }
}
