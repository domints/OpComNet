using System.Collections.Concurrent;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using OpComNet.Tools;

namespace OpComNet;

public sealed class OpComCable : IDisposable
{
    private bool _isoTPFix;
    private readonly SerialPort _port;

    private readonly ConcurrentQueue<CanMessage> _writeQueue;
    private readonly BlockingCollection<CanMessage> _readQueue;
    private readonly ILogger<OpComCable> _logger;
    private bool _outPacketAwaiting;
    private DateTime _packetSentTime;

    private object _directWriteLock = new();

    public bool NeedRestart { get; private set; }
    public int PacketsToSend => _writeQueue.Count;
    public int PacketsToRead => _readQueue.Count;

    public OpComCable(ILogger<OpComCable> logger)
    {
        _writeQueue = new ConcurrentQueue<CanMessage>();
        _readQueue = new BlockingCollection<CanMessage>();
        _port = new SerialPort();
        _logger = logger;
    }

    public bool Open(string portName, int baudRate = 500000)
    {
        _port.BaudRate = baudRate;
        _port.PortName = portName;
        _port.Open();
        Thread.Sleep(100);
        if (!_port.IsOpen)
            return false;

        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();

        return true;
    }

    public void Close()
    {
        if (_port.IsOpen)
            _port.Close();
    }

    public void Write(byte[] data)
    {
        var buffer = new BinaryArrayBuilder();
        buffer.AppendValues("<H", data.Length);
        buffer.AppendBytes(data);
        buffer.AppendByte(CalculateChecksum(buffer.ToArray()));
        var message = buffer.ToArray();
        _port.Write(message, 0, message.Length);
        while (_port.BytesToWrite > 0)
            Thread.Sleep(1);
    }

    public byte[] Read()
    {
        var msgSizeBfr = new byte[2];
        _port.Read(msgSizeBfr, 0, 2);
        var msgSize = StructPacker.UnpackSingle<ushort>("<H", msgSizeBfr);
        var msgBfr = new byte[msgSize];
        _port.Read(msgBfr, 0, msgSize);
        var chksmBfr = new byte[1];
        _port.Read(chksmBfr, 0, 1);
        var checksum = CalculateChecksum(msgSizeBfr.Concat(msgBfr).ToArray());
        if (checksum != chksmBfr[0])
            throw new InvalidDataException("Checksum is invalid!");

        if (msgBfr.Length == 3 && msgBfr.All(b => b == 0x7F))
            NeedRestart = true;

        return msgBfr;
    }

    public byte[] Execute(byte[] data)
    {
        Write(data);
        return Read();
    }

    public byte[] Execute(string hexString)
    {
        return Execute(Others.StringToByteArray(hexString));
    }

    public void Init(BusType bus, bool isoTPFix = false)
    {
        _isoTPFix = isoTPFix;
        Execute("AB");
        Execute("AA");
        Execute("AC01");
        Execute("74");
        Execute("730100F6");
        Execute("730230EC");
        Execute("7303");
        Execute("8e02");
        Execute("8402");

        switch (bus)
        {
            case BusType.SWCAN:
                InitSWCAN();
                break;
            case BusType.MSCAN:
                InitMSCAN();
                break;
            case BusType.HSCAN:
                InitHSCAN();
                break;
            default:
                throw new InvalidOperationException("Unknown CAN bus type!");
        }
    }

    private void InitSWCAN()
    {
        Execute("2021");
        Execute("8403");
        Execute("8108043c030303");
    }

    private void InitMSCAN()
    {
        Execute("2022");
        Execute("2024");
        Execute("8e01");
        Execute("81080235010101");
    }

    private void InitHSCAN()
    {
        Execute("2022");
        Execute("2023");
        Execute("8e01");
        Execute("8102");
    }

    public void SetCANFilter(int[] filters)
    {
        Execute("8202");
        for (int i = 0; i < 8; i++)
        {
            Execute(StructPacker.Pack("<BBi", 0x83, i + 1, i < filters.Length - 1 ? filters[i] : 0x00));
        }
        Execute("8201");
    }

    public void CANWrite(CanMessage message)
    {
        _writeQueue.Enqueue(message);
    }

    /// <summary>
    /// Directly writes packet to CAN Bus. Skips the queue, so don't abuse it, otherwise queue will never clear.
    /// </summary>
    /// <param name="message">Message to send</param>
    public bool DirectWriteCANMessage(CanMessage message)
    {
        lock (_directWriteLock)
        {
            if (_outPacketAwaiting)
                return false;

            _outPacketAwaiting = true;
        }

        var data = FixIsoTP(message.Data);
        var msg = new BinaryArrayBuilder();
        msg.AppendValues("<BIB", 0x90, message.ArbitrationId, data.Length);
        msg.AppendBytes(data);

        Write(msg.ToArray());
        _packetSentTime = DateTime.Now;
        return true;
    }

    public void CANMultiRead(CanMessage message)
    {
        var data = FixIsoTP(message.Data);
        var msg = new BinaryArrayBuilder();
        msg.AppendValues("<BIB", 0x71, message.ArbitrationId, data.Length);
        msg.AppendBytes(data);

        Execute(msg.ToArray());
    }

    public void CANStopMultiRead(CanMessage message)
    {
        var data = FixIsoTP(message.Data);
        var msg = new BinaryArrayBuilder();
        msg.AppendValues("<BIB", 0x72, message.ArbitrationId, data.Length);
        msg.AppendBytes(data);

        Execute(msg.ToArray());
    }

    public CanMessage? CANRead()
    {
        if (_readQueue.TryTake(out CanMessage? msg, 5000))
            return msg;

        return null;
    }

    public void Dispose()
    {
        Close();
    }

    private void CANWorker(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_port.BytesToRead >= 2)
            {
                var readBytes = Read();
                if (NeedRestart)
                {
                    break;
                }
                if (readBytes[0] == 0x91)
                {
                    var (pid, dataLength) = StructPacker.Unpack<(int, byte)>(">IB", readBytes[1..6]);
                    var readData = readBytes[6..(6 + dataLength)];
                    var readMsg = new CanMessage(pid, readData);
                    _readQueue.Add(readMsg, cancellationToken);
                }
                else if (readBytes[0] == 0xD0)
                {
                    _outPacketAwaiting = false;
                }
            }

            if (!_outPacketAwaiting && _writeQueue.TryDequeue(out CanMessage? writeMsg))
            {
                DirectWriteCANMessage(writeMsg);
            }

            if (_outPacketAwaiting && _packetSentTime.AddMilliseconds(500) < DateTime.Now)
            {
                _outPacketAwaiting = false;
                _logger.LogWarning("Packet send timeout!");
            }
        }
    }

    private static byte CalculateChecksum(byte[] data)
    {
        var sum = data.Sum(d => d);
        return (byte)(sum & 0xFF);
    }

    private byte[] FixIsoTP(byte[] data)
    {
        if (_isoTPFix && data.Length >= 1 && data.Length < 8 && data[0] == data.Length - 1)
        {
            byte[] fixedData = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                if (i < data.Length)
                    fixedData[i] = data[i];
                else
                    fixedData[i] = 0xAA;
            }

            return fixedData;
        }
        else
        {
            return data;
        }
    }
}