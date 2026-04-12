using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.IO.Ports;
using MinimalGCS.Mavlink;

namespace MinimalGCS.Connection
{
    public abstract class MavLinkInterface : IDisposable
    {
        public abstract void Send(byte[] data);
        public abstract void StartReading(Action<byte[]> onDataReceived);
        public abstract void Close();
        public abstract bool IsOpen { get; }
        public abstract string Name { get; }

        public abstract void Dispose();
    }

    public class UdpInterface : MavLinkInterface
    {
        private UdpClient? _client;
        private IPEndPoint? _remoteEp;
        private bool _running;
        private int _port;

        public UdpInterface(int port = 14550)
        {
            _port = port;
            _client = new UdpClient(_port);
            _client.Client.ReceiveTimeout = 1000;
        }

        public override string Name => $"UDP:{_port}";
        public override bool IsOpen => _client != null;

        public override void Send(byte[] data)
        {
            if (_client != null && _remoteEp != null)
            {
                _client.Send(data, data.Length, _remoteEp);
            }
        }

        public override void StartReading(Action<byte[]> onDataReceived)
        {
            _running = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                while (_running && _client != null)
                {
                    try
                    {
                        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = _client.Receive(ref from);
                        _remoteEp = from;
                        onDataReceived(data);
                    }
                    catch (SocketException) { /* Timeout or closed */ }
                    catch (ObjectDisposedException) { break; }
                }
            });
        }

        public override void Close()
        {
            _running = false;
            _client?.Close();
            _client = null;
        }

        public override void Dispose() => Close();
    }

    public class SerialInterface : MavLinkInterface
    {
        private SerialPort? _port;
        private bool _running;

        public SerialInterface(string portName, int baudRate)
        {
            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
            _port.ReadTimeout = 1000;
            _port.Open();
        }

        public override string Name => $"{_port?.PortName}@{_port?.BaudRate}";
        public override bool IsOpen => _port != null && _port.IsOpen;

        public override void Send(byte[] data)
        {
            if (_port != null && _port.IsOpen)
            {
                _port.Write(data, 0, data.Length);
            }
        }

        public override void StartReading(Action<byte[]> onDataReceived)
        {
            _running = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                byte[] buffer = new byte[4096];
                while (_running && _port != null && _port.IsOpen)
                {
                    try
                    {
                        int count = _port.Read(buffer, 0, buffer.Length);
                        if (count > 0)
                        {
                            byte[] actual = new byte[count];
                            Buffer.BlockCopy(buffer, 0, actual, 0, count);
                            onDataReceived(actual);
                        }
                    }
                    catch (TimeoutException) { }
                    catch (Exception) { break; }
                }
            });
        }

        public override void Close()
        {
            _running = false;
            if (_port != null && _port.IsOpen)
            {
                _port.Close();
            }
            _port = null;
        }

        public override void Dispose() => Close();
    }

    public class TcpInterface : MavLinkInterface
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private bool _running;
        private string _host;
        private int _port;

        public TcpInterface(string host, int port)
        {
            _host = host;
            _port = port;
            _client = new TcpClient();
            _client.Connect(_host, _port);
            _stream = _client.GetStream();
        }

        public override string Name => $"TCP:{_host}:{_port}";
        public override bool IsOpen => _client != null && _client.Connected;

        public override void Send(byte[] data)
        {
            try
            {
                _stream?.Write(data, 0, data.Length);
            }
            catch { }
        }

        public override void StartReading(Action<byte[]> onDataReceived)
        {
            _running = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                byte[] buffer = new byte[4096];
                while (_running && _stream != null)
                {
                    try
                    {
                        int count = _stream.Read(buffer, 0, buffer.Length);
                        if (count > 0)
                        {
                            byte[] actual = new byte[count];
                            Buffer.BlockCopy(buffer, 0, actual, 0, count);
                            onDataReceived(actual);
                        }
                        else { break; }
                    }
                    catch { break; }
                }
            });
        }

        public override void Close()
        {
            _running = false;
            _stream?.Close();
            _client?.Close();
            _client = null;
            _stream = null;
        }

        public override void Dispose() => Close();
    }
}
