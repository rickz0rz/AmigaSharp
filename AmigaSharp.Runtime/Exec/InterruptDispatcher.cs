using AmigaSharp.Runtime.Hardware;

namespace AmigaSharp.Runtime.Exec;

/// <summary>
/// Delivers the interrupts of the custom chips to the handlers and the servers that the program installed with exec
/// SetIntVector and AddIntServer. It does what the interrupt code of exec does.
/// </summary>
/// <remarks>
/// Translated code runs as C#, so an interrupt cannot stop it at any instruction. The runtime calls
/// <see cref="Core.Poll"/> at safe points instead: in each library call, in the waits, in the interpreter, and at each
/// backward branch of the translated code. An interrupt is delivered if INTENA enables it, INTREQ requests it, and
/// its level is higher than the interrupt mask of the CPU. exec Disable clears the master enable bit of INTENA.
/// </remarks>
public sealed class InterruptDispatcher(Core core)
{
    // The interrupts that have a chain of servers. For these, exec clears the request after the servers.
    private static readonly int[] ServerChains = [3, 4, 5, 13, 15];

    private const uint InterruptVectorSize = 12;

    /// <summary>The interrupts that the dispatcher delivered, by INTREQ bit. For tests and for debugging.</summary>
    public long[] Delivered { get; } = new long[16];

    /// <summary>Delivers each interrupt that is pending and enabled.</summary>
    public void Deliver()
    {
        var custom = core.Chipset.Custom;
        var serviced = 0;
        while (true)
        {
            if ((custom.Intena & (1 << InterruptBit.Enable)) == 0)
                return;
            // A handler that does not clear its request does not run again in the same poll.
            var pending = custom.Intena & custom.Intreq & 0x3FFF & ~serviced;
            if (pending == 0)
                return;

            var level = Enumerable.Range(0, 14).Where(bit => (pending & (1 << bit)) != 0).Max(InterruptBit.Level);
            if (level <= core.Cpu.InterruptMask)
                return;

            var bits = Enumerable.Range(0, 14).Where(bit => (pending & (1 << bit)) != 0 && InterruptBit.Level(bit) == level);
            ServiceLevel(level, bits.ToList());
            foreach (var bit in bits)
                serviced |= 1 << bit;
        }
    }

    /// <summary>True if an interrupt that INTENA enables has a handler or a server. Then a wait can end.</summary>
    public bool CanInterrupt()
    {
        var custom = core.Chipset.Custom;
        if ((custom.Intena & (1 << InterruptBit.Enable)) == 0)
            return false;
        for (var bit = 0; bit < 14; bit++)
        {
            if ((custom.Intena & (1 << bit)) == 0)
                continue;
            var vector = Vector(bit);
            var memory = core.Memory;
            if (ServerChains.Contains(bit) ? !ExecList.IsEmpty(memory, memory.Read32(vector)) : memory.Read32(vector + 4) != 0)
                return true;
        }

        return false;
    }

    private void ServiceLevel(int level, List<int> bits)
    {
        var cpu = core.Cpu;
        var memory = core.Memory;
        var custom = core.Chipset.Custom;
        var saved = CpuSnapshot.Take(cpu);

        // The CPU takes the interrupt in supervisor mode, and the mask blocks the interrupts of this level and below.
        cpu.Sr = (ushort)(0x2000 | (level << 8) | cpu.Ccr);
        try
        {
            foreach (var bit in bits)
            {
                var vector = Vector(bit);
                if (ServerChains.Contains(bit))
                {
                    Delivered[bit]++;
                    foreach (var server in ExecList.Nodes(memory, memory.Read32(vector)).ToList())
                    {
                        Call(memory.Read32(server + InterruptOffsets.Code), memory.Read32(server + InterruptOffsets.Data),
                            custom.Intena & custom.Intreq);
                        // A server returns with Z clear if no other server must run for this interrupt.
                        if (!cpu.Z)
                            break;
                    }

                    custom.Write(CustomRegister.Intreq, (ushort)(1 << bit));
                    continue;
                }

                var code = memory.Read32(vector + 4);
                if (code == 0)
                {
                    // Nothing handles the interrupt yet. The request stays, so that a handler that the program installs
                    // later gets it. For example, a serial byte waits in SERDATR.
                    continue;
                }

                // A handler clears its request itself.
                Delivered[bit]++;
                Call(code, memory.Read32(vector), custom.Intena & custom.Intreq);
            }
        }
        finally
        {
            saved.Restore(cpu);
        }
    }

    /// <summary>Calls a handler or a server with the registers that exec gives it.</summary>
    private void Call(uint code, uint data, int requests)
    {
        var cpu = core.Cpu;
        cpu.A[0] = CustomRegister.Base;
        cpu.A[1] = data;
        cpu.A[5] = code;
        cpu.A[6] = core.ExecBase;
        cpu.D[1] = (uint)requests;
        core.CallFromNative(code);
    }

    private uint Vector(int bit) => core.ExecBase + ExecBaseOffsets.InterruptVectors + (uint)bit * InterruptVectorSize;
}

/// <summary>A copy of all registers of the CPU. An interrupt does not change the registers of the code that it stops.</summary>
public readonly record struct CpuSnapshot(uint[] D, uint[] A, ushort Sr, uint Usp, uint Ssp, uint Pc)
{
    public static CpuSnapshot Take(Cpu.CpuState cpu) =>
        new((uint[])cpu.D.Clone(), (uint[])cpu.A.Clone(), cpu.Sr, cpu.Usp, cpu.Ssp, cpu.Pc);

    public void Restore(Cpu.CpuState cpu)
    {
        cpu.Sr = Sr;
        cpu.Usp = Usp;
        cpu.Ssp = Ssp;
        Array.Copy(D, cpu.D, 8);
        Array.Copy(A, cpu.A, 7);
        cpu.Pc = Pc;
    }
}
