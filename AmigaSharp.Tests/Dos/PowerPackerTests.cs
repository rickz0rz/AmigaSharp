using System.Text;
using AmigaSharp.Runtime.Dos;
using AmigaSharp.Tests.Translator;

namespace AmigaSharp.Tests.Dos;

public class PowerPackerTests
{
    // A small PowerPacker file: 20 bytes that unpack to 9 bytes.
    private static readonly byte[] Small =
    [
        0x50, 0x50, 0x32, 0x30, 0x09, 0x0A, 0x0C, 0x0D, 0xAC, 0x8C, 0x82, 0x03,
        0x0C, 0x00, 0x96, 0xAC, 0x00, 0x00, 0x09, 0x0D,
    ];

    [Fact]
    public void Unpack_SmallFile()
    {
        Assert.True(PowerPacker.IsPacked(Small));
        Assert.Equal("51\u00000\u00000\u00000\u0000", Encoding.Latin1.GetString(PowerPacker.Unpack(Small)));
    }

    [Fact]
    public void Unpack_DataThatIsNotPacked_Throws()
    {
        var data = Encoding.ASCII.GetBytes("not packed data");

        Assert.False(PowerPacker.IsPacked(data));
        Assert.Throws<InvalidDataException>(() => PowerPacker.Unpack(data));
    }

    [Fact]
    public void Unpack_DamagedData_Throws()
    {
        var damaged = (byte[])Small.Clone();
        // A larger size in the trailer: the bits end too soon.
        damaged[^2] = 0x40;

        Assert.Throws<InvalidDataException>(() => PowerPacker.Unpack(damaged));
    }

    [Fact]
    public void Unpack_ListingFileOfTheDrive()
    {
        var path = Path.Combine(TargetProgram.DriveDirectory, "curday.dat");
        if (!File.Exists(path))
            Assert.Skip("The drive of the Prevue machine is missing.");

        var unpacked = PowerPacker.Unpack(File.ReadAllBytes(path));

        Assert.Equal(22_818, unpacked.Length);
        Assert.StartsWith("BE3366N", Encoding.Latin1.GetString(unpacked, 0, 16));
    }
}
