namespace AmigaSharp.Runtime.Libraries;

/// <summary>
/// Gives the register of a parameter of a library function. The parameter type can be <see cref="uint"/>,
/// <see cref="int"/>, <see cref="ushort"/>, <see cref="short"/>, <see cref="byte"/> or <see cref="bool"/>. A
/// <see cref="bool"/> is true if the register is not zero.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public abstract class RegisterAttribute(bool isAddressRegister, int number) : Attribute
{
    public bool IsAddressRegister { get; } = isAddressRegister;
    public int Number { get; } = number;
}

public sealed class D0Attribute() : RegisterAttribute(false, 0);
public sealed class D1Attribute() : RegisterAttribute(false, 1);
public sealed class D2Attribute() : RegisterAttribute(false, 2);
public sealed class D3Attribute() : RegisterAttribute(false, 3);
public sealed class D4Attribute() : RegisterAttribute(false, 4);
public sealed class D5Attribute() : RegisterAttribute(false, 5);
public sealed class D6Attribute() : RegisterAttribute(false, 6);
public sealed class D7Attribute() : RegisterAttribute(false, 7);
public sealed class A0Attribute() : RegisterAttribute(true, 0);
public sealed class A1Attribute() : RegisterAttribute(true, 1);
public sealed class A2Attribute() : RegisterAttribute(true, 2);
public sealed class A3Attribute() : RegisterAttribute(true, 3);
public sealed class A4Attribute() : RegisterAttribute(true, 4);
public sealed class A5Attribute() : RegisterAttribute(true, 5);
public sealed class A6Attribute() : RegisterAttribute(true, 6);
