namespace AmigaSharp.AmigaCore.CPU;

public class M68K
{
    public readonly int[] A = new int[8];
    public readonly int[] D = new int[8];
    public readonly Stack<int> Stack = new();

    public void ClearLong(DataRegister register)
    {
        MoveQuick(register, 0);
    }

    public void LoadEffectiveAddress(AddressRegister register, int address)
    {
        D[(int)register] = address;
    }

    public void MoveLong(DataRegister register, int value)
    {
        MoveQuick(register, value);
    }

    public void MoveQuick(DataRegister register, int value)
    {
        D[(int)register] = value;
    }

    public void MoveAddressLong(AddressRegister register, int address)
    {
        A[(int)register] = address;
    }

    public void MoveMultipleLong(IEnumerable<DataRegister> dataRegisters, IEnumerable<AddressRegister> addressRegisters)
    {
        foreach (var dataRegister in dataRegisters)
        {
            Stack.Push(D[(int)dataRegister]);
        }

        foreach (var addressRegister in addressRegisters)
        {
            Stack.Push(A[(int)addressRegister]);
        }
    }

    public void MoveMultipleLong(IEnumerable<DataRegister> dataRegisters, IEnumerable<AddressRegister> addressRegisters, int address)
    {
    }
}
