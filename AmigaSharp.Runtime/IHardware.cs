namespace AmigaSharp.Runtime;

/// <summary>Memory-mapped hardware: the custom chips and the CIAs.</summary>
public interface IHardware
{
    byte Read8(uint address);
    ushort Read16(uint address);
    void Write8(uint address, byte value);
    void Write16(uint address, ushort value);
}
