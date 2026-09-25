namespace AmigaSharp.Runtime.Libraries.Native;

/// <summary>utility.library: the date functions and the 32-bit arithmetic of V37.</summary>
public class UtilityLibrary(Core core) : AbstractLibrary
{
    // ClockData offsets (utility/date.h). All fields are words.
    private const uint Second = 0;
    private const uint Minute = 2;
    private const uint Hour = 4;
    private const uint Day = 6;
    private const uint Month = 8;
    private const uint Year = 10;
    private const uint WeekDay = 12;

    private static readonly DateTime Epoch = new(1978, 1, 1);

    private readonly Memory _memory = core.Memory;

    public override string Name => "utility.library";
    public override ushort Version => 40;
    public override short LowestOffset => -300;

    // Amiga2Date(seconds, result)
    //            D0       A0
    [LibraryFunctionOffset(-120)]
    public void Amiga2Date([D0] uint seconds, [A0] uint clockData)
    {
        var date = Epoch.AddSeconds(seconds);
        _memory.Write16(clockData + Second, (ushort)date.Second);
        _memory.Write16(clockData + Minute, (ushort)date.Minute);
        _memory.Write16(clockData + Hour, (ushort)date.Hour);
        _memory.Write16(clockData + Day, (ushort)date.Day);
        _memory.Write16(clockData + Month, (ushort)date.Month);
        _memory.Write16(clockData + Year, (ushort)date.Year);
        _memory.Write16(clockData + WeekDay, (ushort)date.DayOfWeek);
    }

    // seconds = Date2Amiga(date)
    // D0                   A0
    [LibraryFunctionOffset(-126)]
    public uint Date2Amiga([A0] uint clockData) => CheckDate(clockData);

    // seconds = CheckDate(date)
    // D0                  A0
    // Returns 0 if the date is not valid.
    [LibraryFunctionOffset(-132)]
    public uint CheckDate([A0] uint clockData)
    {
        int Field(uint offset) => _memory.Read16(clockData + offset);
        var (year, month, day) = (Field(Year), Field(Month), Field(Day));
        if (year is < 1978 or > 2114 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || Field(Hour) > 23 || Field(Minute) > 59 || Field(Second) > 59)
            return 0;
        var date = new DateTime(year, month, day, Field(Hour), Field(Minute), Field(Second));
        return (uint)(date - Epoch).TotalSeconds;
    }

    // result = SMult32(arg1, arg2)
    // D0               D0    D1
    [LibraryFunctionOffset(-138)]
    public int SMult32([D0] int a, [D1] int b) => unchecked(a * b);

    // result = UMult32(arg1, arg2)
    // D0               D0    D1
    [LibraryFunctionOffset(-144)]
    public uint UMult32([D0] uint a, [D1] uint b) => unchecked(a * b);

    // quotient:remainder = SDivMod32(dividend, divisor)
    // D0       D1                    D0        D1
    [LibraryFunctionOffset(-150)]
    public void SDivMod32([D0] int dividend, [D1] int divisor)
    {
        core.Cpu.D[0] = (uint)(dividend / divisor);
        core.Cpu.D[1] = (uint)(dividend % divisor);
    }

    // quotient:remainder = UDivMod32(dividend, divisor)
    // D0       D1                    D0        D1
    [LibraryFunctionOffset(-156)]
    public void UDivMod32([D0] uint dividend, [D1] uint divisor)
    {
        core.Cpu.D[0] = dividend / divisor;
        core.Cpu.D[1] = dividend % divisor;
    }
}
