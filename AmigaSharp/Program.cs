namespace AmigaSharp;

class Program
{
    // Fix cases where i'm expecting D or A registers and use correct instructions
    static int Main(string[] args)
    {
        return HelloWorldTranslated.Run(new AmigaCore.Amiga());
    }
}
