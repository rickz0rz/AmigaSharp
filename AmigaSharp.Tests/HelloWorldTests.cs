using System.Text;
using AmigaSharp.Runtime;

namespace AmigaSharp.Tests;

public class HelloWorldTests
{
    [Fact]
    public void Run_PrintsHelloWorldOnce_AndReturnsZero()
    {
        var output = new MemoryStream();

        var result = HelloWorldTranslated.Run(new Core(output));

        Assert.Equal(0u, result);
        Assert.Equal("Hello World!\n", Encoding.Latin1.GetString(output.ToArray()));
    }
}
