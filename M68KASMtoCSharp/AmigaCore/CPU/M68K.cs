namespace M68KASMtoCSharp.AmigaCore.CPU;

public class M68K
{
    private readonly Dictionary<AddressRegister, int> _addressRegisters;
    private readonly Dictionary<DataRegister, int> _dataRegisters;

    public M68K()
    {
        _addressRegisters = new Dictionary<AddressRegister, int>();
        _dataRegisters = new Dictionary<DataRegister, int>();
    }

    public void SetAddressRegister(AddressRegister register, int value)
    {
        _addressRegisters[register] = value;
    }

    public void SetDataRegister(DataRegister register, int value)
    {
        _dataRegisters[register] = value;
    }

    public void ClearLong(DataRegister register)
    {
        SetDataRegister(register, 0);
    }

    public int GetAddressRegister(AddressRegister register)
    {
        return _addressRegisters.GetValueOrDefault(register, 0);
    }

    public int GetDataRegister(DataRegister register)
    {
        return _dataRegisters.GetValueOrDefault(register, 0);
    }

    public void LoadEffectiveAddress(int address, AddressRegister register)
    {
        SetAddressRegister(register, address);
    }

    public void MoveLong(int value, DataRegister register)
    {
        MoveQuick(value, register);
    }

    public void MoveQuick(int value, DataRegister register)
    {
        SetDataRegister(register, value);
    }

    public void MoveAddressLong(int address, AddressRegister register)
    {
        SetAddressRegister(register, address);
    }
}
