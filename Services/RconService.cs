using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArcanePlayConnect.Services;

public class RconService
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _requestId;
    private readonly LoggingService _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsConnected => _client?.Connected == true;

    public event Action? ConnectionChanged;

    public RconService(LoggingService logger)
    {
        _logger = logger;
    }

    public async Task<bool> ConnectAsync(string ip, int port, string password)
    {
        try
        {
            Disconnect();
            _client = new TcpClient();
            _client.ReceiveTimeout = 5000;
            _client.SendTimeout = 5000;
            await _client.ConnectAsync(ip, port);
            _stream = _client.GetStream();

            // Send auth packet (type 3) and read the response
            var authId = Interlocked.Increment(ref _requestId);
            await WritePacketAsync(authId, 3, password);
            var authResponse = await ReadPacketAsync();

            // PaperMC returns -1 as RequestId on bad password
            if (authResponse.RequestId == -1)
            {
                _logger.LogError("RCON authentication failed. Check password.");
                Disconnect();
                return false;
            }

            _logger.LogInfo($"RCON connected to {ip}:{port}");
            ConnectionChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"RCON connection failed: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        try
        {
            _stream?.Close();
            _client?.Close();
        }
        catch { }
        finally
        {
            _stream = null;
            _client = null;
            ConnectionChanged?.Invoke();
        }
    }

    public async Task<string> SendCommand(string command)
    {
        if (!IsConnected || _stream == null)
        {
            _logger.LogError("RCON not connected. Cannot send command.");
            return string.Empty;
        }

        // Strip leading slash — PaperMC RCON does not want it
        if (command.StartsWith('/'))
            command = command[1..];

        await _lock.WaitAsync();
        try
        {
            var cmdId = Interlocked.Increment(ref _requestId);

            // Send command and read exactly one response — PaperMC sends one packet per command
            await WritePacketAsync(cmdId, 2, command);
            var pkt = await ReadPacketAsync();

            var response = pkt.Body ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(response))
                _logger.LogInfo($"RCON response: {response}");

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError($"RCON command error: {ex.Message}");
            Disconnect();
            return string.Empty;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task WritePacketAsync(int requestId, int type, string body)
    {
        if (_stream == null) return;
        var packet = BuildPacket(requestId, type, body);
        await _stream.WriteAsync(packet);
        await _stream.FlushAsync();
    }

    private static byte[] BuildPacket(int requestId, int type, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        // Payload = id(4) + type(4) + body(N) + null(1) + null(1)
        var payloadSize = 4 + 4 + bodyBytes.Length + 2;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(payloadSize);       // length field (not included in count)
        writer.Write(requestId);
        writer.Write(type);
        writer.Write(bodyBytes);
        writer.Write((byte)0);           // null terminator for body
        writer.Write((byte)0);           // padding null
        return ms.ToArray();
    }

    private async Task<(int RequestId, int Type, string Body)> ReadPacketAsync()
    {
        if (_stream == null) return (-1, -1, string.Empty);

        // Read the 4-byte length field
        var sizeBuffer = new byte[4];
        await ReadExactAsync(_stream, sizeBuffer, 4);
        var size = BitConverter.ToInt32(sizeBuffer, 0);

        if (size < 10)
        {
            // Malformed packet — drain and return empty
            if (size > 0)
            {
                var drain = new byte[size];
                await ReadExactAsync(_stream, drain, size);
            }
            return (-1, -1, string.Empty);
        }

        var payload = new byte[size];
        await ReadExactAsync(_stream, payload, size);

        var requestId = BitConverter.ToInt32(payload, 0);
        var type      = BitConverter.ToInt32(payload, 4);
        // body runs from byte 8 to size-2 (excluding the two null terminators)
        var bodyLen = size - 10;
        var body = bodyLen > 0 ? Encoding.UTF8.GetString(payload, 8, bodyLen) : string.Empty;

        return (requestId, type, body);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0) throw new IOException("RCON connection closed by server.");
            offset += read;
        }
    }
}
