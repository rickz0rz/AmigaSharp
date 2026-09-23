using AmigaSharp.Runtime;

namespace AmigaSharp;

class Program
{
    static int Main(string[] args)
    {
        return (int)HelloWorldTranslated.Run(new Core());
    }
}
