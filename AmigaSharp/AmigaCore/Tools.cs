using System.Text;

namespace AmigaSharp.AmigaCore;

public static class Tools
{
    public static string ReadStringUntilNull(Amiga amiga, int address)
    {
        var stringBuilder = new StringBuilder();

        var cAddress = address;
        var cValue = amiga.Memory[cAddress];
        while (cValue != 0)
        {
            stringBuilder.Append((char)cValue);
            cAddress++;
            cValue = amiga.Memory[cAddress];
        }

        return stringBuilder.ToString();
    }
}
