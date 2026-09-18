using System.Text;
using M68KASMtoCSharp.AmigaCore;

namespace M68KASMtoCSharp;

public static class Tools
{
    public static string ReadStringUntilNull(Amiga amiga, int address)
    {
        var stringBuilder = new StringBuilder();

        var cAddress = address;
        var cValue = amiga.ReadByteMemory(cAddress);
        while (cValue != 0)
        {
            stringBuilder.Append((char)cValue);
            cAddress++;
            cValue = amiga.ReadByteMemory(cAddress);
        }

        return stringBuilder.ToString();
    }
}
