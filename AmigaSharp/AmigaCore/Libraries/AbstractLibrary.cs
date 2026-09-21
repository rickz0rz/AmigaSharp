namespace AmigaSharp.AmigaCore.Libraries;

public abstract class AbstractLibrary(Amiga amiga)
{
    protected Amiga Amiga = amiga;

    public virtual int? InitialOffset()
    {
        return null;
    }

    public virtual string Name()
    {
        return null;
    }
}
