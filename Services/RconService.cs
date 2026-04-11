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

    // Stored credentials for automatic reconnection
    private string _lastIp = string.Empty;
    private int _lastPort;
    private string _lastPassword = string.Empty;

    // Keep-alive heartbeat
    private Timer? _keepAliveTimer;
    private const int KeepAliveIntervalMs = 15_000; // 15 seconds

    /// <summary>
    /// Checks whether the RCON connection is alive by verifying the socket state.
    /// Unlike TcpClient.Connected, this detects half-open / server-closed connections.
    /// </summary>
    public bool IsConnected
    {
        get
        {
            try
            {
                if (_client?.Client == null || !_client.Client.Connected || _stream == null)
                    return false;

                // Poll the socket to detect if the remote end has closed.
                // Poll with SelectRead: if readable AND no data available ? connection closed.
                if (_client.Client.Poll(0, SelectMode.SelectRead))
                {
                    // If DataAvailable is false the remote end has closed the connection.
                    if (!_stream.DataAvailable)
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

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
            _client.ReceiveTimeout = 10_000; // 10 s - generous for slow servers
            _client.SendTimeout = 10_000;

            await _client.ConnectAsync(ip, port);

            // Enable TCP keep-alive at the OS level to detect dead connections
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            ConfigureTcpKeepAlive(_client.Client);

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

            // Store credentials for auto-reconnect
            _lastIp = ip;
            _lastPort = port;
            _lastPassword = password;

            // Start keep-alive heartbeat - sends a lightweight command periodically
            // to prevent the Minecraft RCON server from closing an idle connection.
            StartKeepAlive();

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
        StopKeepAlive();
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
        // Strip leading slash - PaperMC RCON does not want it
        if (command.StartsWith('/'))
            command = command[1..];

        // Try once, and if it fails due to a broken connection, attempt one reconnect
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!IsConnected)
            {
                if (attempt == 0 && CanAutoReconnect())
                {
                    _logger.LogInfo("RCON connection lost. Attempting auto-reconnect...");
                    var reconnected = await TryReconnectAsync();
                    if (!reconnected)
                    {
                        _logger.LogError("RCON auto-reconnect failed. Cannot send command.");
                        return string.Empty;
                    }
                }
                else
                {
                    _logger.LogError("RCON not connected. Cannot send command.");
                    return string.Empty;
                }
            }

            await _lock.WaitAsync();
            try
            {
                var cmdId = Interlocked.Increment(ref _requestId);

                await WritePacketAsync(cmdId, 2, command);

                // Read packets until we find the one matching our request ID.
                // Stale responses (e.g. from keep-alive) may be sitting in the
                // stream buffer and must be drained to avoid mismatched replies.
                const int maxDrainAttempts = 5;
                string response = string.Empty;
                for (int i = 0; i < maxDrainAttempts; i++)
                {
                    var pkt = await ReadPacketAsync();
                    if (pkt.RequestId == cmdId)
                    {
                        response = pkt.Body ?? string.Empty;
                        break;
                    }
                    // Not our packet - discard and read the next one
                }

                if (!string.IsNullOrWhiteSpace(response))
                    _logger.LogInfo($"RCON response: {response}");

                return response;
            }
            catch (Exception ex) when (attempt == 0)
            {
                _logger.LogWarning($"RCON command failed ({ex.Message}). Reconnecting...");
                CleanupSocket();
                ConnectionChanged?.Invoke();

                if (CanAutoReconnect())
                {
                    var reconnected = await TryReconnectAsync();
                    if (reconnected)
                        continue; // retry the command
                }

                _logger.LogError("RCON reconnect failed after command error.");
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError($"RCON command error: {ex.Message}");
                CleanupSocket();
                ConnectionChanged?.Invoke();
                return string.Empty;
            }
            finally
            {
                _lock.Release();
            }
        }

        return string.Empty;
    }

    // ?? Keep-alive heartbeat ????????????????????????????????????????????????

    private void StartKeepAlive()
    {
        StopKeepAlive();
        _keepAliveTimer = new Timer(OnKeepAliveTick, null, KeepAliveIntervalMs, KeepAliveIntervalMs);
    }

    private void StopKeepAlive()
    {
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
    }

    private async void OnKeepAliveTick(object? state)
    {
        if (!IsConnected) return;

        try
        {
            // Send a no-op list command - lightweight, always succeeds
            await SendCommand("list");
        }
        catch
        {
            // Swallow - SendCommand handles reconnect internally
        }
    }

    // ?? Auto-reconnect ?????????????????????????????????????????????????????

    private bool CanAutoReconnect()
    {
        return !string.IsNullOrEmpty(_lastIp) && _lastPort > 0 && !string.IsNullOrEmpty(_lastPassword);
    }

    private async Task<bool> TryReconnectAsync()
    {
        try
        {
            CleanupSocket();

            _client = new TcpClient();
            _client.ReceiveTimeout = 10_000;
            _client.SendTimeout = 10_000;

            await _client.ConnectAsync(_lastIp, _lastPort);

            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            ConfigureTcpKeepAlive(_client.Client);

            _stream = _client.GetStream();

            var authId = Interlocked.Increment(ref _requestId);
            await WritePacketAsync(authId, 3, _lastPassword);
            var authResponse = await ReadPacketAsync();

            if (authResponse.RequestId == -1)
            {
                _logger.LogError("RCON auto-reconnect auth failed.");
                CleanupSocket();
                return false;
            }

            StartKeepAlive();
            _logger.LogInfo($"RCON reconnected to {_lastIp}:{_lastPort}");
            ConnectionChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"RCON reconnect failed: {ex.Message}");
            CleanupSocket();
            ConnectionChanged?.Invoke();
            return false;
        }
    }

    /// <summary>
    /// Silently disposes the socket without firing events - used before reconnect.
    /// </summary>
    private void CleanupSocket()
    {
        StopKeepAlive();
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        _stream = null;
        _client = null;
    }

    // ?? TCP keep-alive at OS level ??????????????????????????????????????????

    /// <summary>
    /// Configures OS-level TCP keep-alive probes so the kernel detects dead peers
    /// even if the application is idle. Works on Windows, Linux, and macOS.
    /// </summary>
    private static void ConfigureTcpKeepAlive(Socket socket)
    {
        try
        {
            // Keep-alive time: send first probe after 10s of idle
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            // Keep-alive interval: retry every 5s
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            // Keep-alive retry count: give up after 3 failed probes
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch
        {
            // Some platforms may not support all options - ignore
        }
    }

    // ?? Packet I/O ??????????????????????????????????????????????????????????

    private async Task WritePacketAsync(int requestId, int type, string body)
    {
        if (_stream == null) throw new IOException("RCON stream is null.");
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
        if (_stream == null) throw new IOException("RCON stream is null.");

        // Read the 4-byte length field
        var sizeBuffer = new byte[4];
        await ReadExactAsync(_stream, sizeBuffer, 4);
        var size = BitConverter.ToInt32(sizeBuffer, 0);

        if (size < 10)
        {
            // Malformed packet - drain and return empty
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
