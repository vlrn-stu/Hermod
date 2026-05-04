using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Hermod.LoRa2MQTT.DeviceMocker;

/// <summary>
/// Thin transmit-only wrapper around a Waveshare LoRa module in transparent
/// stream mode. Writes bytes straight to the serial port; the module puts
/// them on the air. No AT configuration here: the receiving
/// WaveshareLoRaAdapter is the one expected to program channel/SF/NetID.
/// </summary>
public sealed class SerialLoRaSender : IDisposable
{
    private readonly ILogger<SerialLoRaSender> _logger;
    private readonly SerialPort _port;
    private readonly bool _appendLf;

    public SerialLoRaSender(ILogger<SerialLoRaSender> logger, string portName, int baudRate, bool appendLf)
    {
        _logger = logger;
        _appendLf = appendLf;
        _port = new SerialPort(portName, baudRate)
        {
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            ReadTimeout = 500,
            WriteTimeout = 2000,
            Encoding = Encoding.ASCII,
        };
    }

    public void Open()
    {
        if (_port.IsOpen) return;
        _port.Open();
        _port.DtrEnable = true;
        _port.RtsEnable = true;
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();
        _logger.LogInformation(
            "Serial port {Port} opened at {Baud} baud (DTR=on, RTS=on)",
            _port.PortName, _port.BaudRate);
    }

    public void SendFrameLogged(string payload)
    {
        SendFrame(payload);
        _logger.LogInformation("tx {Len}B: {Payload}", payload.Length, payload);
    }

    public void SendRaw(ReadOnlySpan<byte> bytes)
    {
        if (!_port.IsOpen) throw new InvalidOperationException("Port not open");
        _port.Write(bytes.ToArray(), 0, bytes.Length);
    }

    public void SendFrame(string payload)
    {
        var framed = _appendLf && !payload.EndsWith('\n') ? payload + "\n" : payload;
        var bytes = Encoding.ASCII.GetBytes(framed);
        SendRaw(bytes);
    }

    public void Dispose()
    {
        try
        {
            if (_port.IsOpen) _port.Close();
        }
        catch
        {
        }
        _port.Dispose();
    }
}
