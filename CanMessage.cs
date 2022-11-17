using System.Text;

namespace OpComNet;

public record CanMessage
{
    public int ArbitrationId { get; }
    public byte[] Data { get; }

    public CanMessage(int arbitrationId, byte[] data)
    {
        ArbitrationId = arbitrationId;
        if (data.Length > 8)
            throw new ArgumentOutOfRangeException(nameof(data), "Data cannot be longer than 8 bytes");

        Data = data;
    }

    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.AppendFormat("PID = {0:X3}, Data = {1}", ArbitrationId, string.Join(" ", Data.Select(b => b.ToString("X2"))));
        return true;
    }
}