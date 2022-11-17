namespace OpComNet.Tools;

public class BinaryArrayBuilder
{
    private readonly MemoryStream _innerStream;

    public BinaryArrayBuilder()
    {
        _innerStream = new MemoryStream();
    }

    public BinaryArrayBuilder(byte[] initialBuffer)
    {
        _innerStream = new MemoryStream(initialBuffer);
    }

    public void AppendByte(byte value)
    {
        _innerStream.WriteByte(value);
    }

    public void AppendBytes(byte[] values)
    {
        _innerStream.Write(values);
    }

    public void AppendValues(string format, params object[] values)
    {
        AppendBytes(StructPacker.Pack(format, values));
    }

    public byte[] ToArray() => _innerStream.ToArray();
}