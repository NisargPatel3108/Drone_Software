using System;
using System.IO.Ports;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MinimalGCS.Mavlink;

namespace MinimalGCS.Connection
{
    public class DiscoveredDevice
    {
        public MavLinkInterface Interface { get; set; } = null!;
        public byte SysId { get; set; }
        public byte CompId { get; set; }
        public DateTime LastHeartbeat { get; set; }

        public override string ToString() => $"{Interface.Name} (SysID: {SysId})";
    }

    public class AutoConnector
    {
        private readonly List<MavLinkInterface> _probingInterfaces = new List<MavLinkInterface>();
        public readonly ConcurrentDictionary<string, DiscoveredDevice> ConnectedDevices = new ConcurrentDictionary<string, DiscoveredDevice>();
        private readonly object _lock = new object();

        public event Action<DiscoveredDevice>? OnDeviceConnected;
        public event Action<string>? OnDeviceDisconnected;

        private CancellationTokenSource _cts = new CancellationTokenSource();

        public void Start()
        {
            StartBackend();
            Task.Run(() => ScanningLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts.Cancel();
            lock (_lock)
            {
                foreach (var device in ConnectedDevices.Values) device.Interface.Close();
                foreach (var iface in _probingInterfaces) iface.Close();
                _probingInterfaces.Clear();
                ConnectedDevices.Clear();
            }
        }

        private void StartBackend()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "mavproxy.exe",
                    Arguments = "--master=tcp:127.0.0.1:5760 --out=udp:127.0.0.1:14550 --out=udp:127.0.0.1:14551 --nodefaults",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }

        private async Task ScanningLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var now = DateTime.Now;
                foreach (var kvp in ConnectedDevices.ToList())
                {
                    if ((now - kvp.Value.LastHeartbeat).TotalSeconds > 10)
                    {
                        if (ConnectedDevices.TryRemove(kvp.Key, out var device))
                        {
                            device.Interface.Close();
                            OnDeviceDisconnected?.Invoke(kvp.Key);
                        }
                    }
                }

                CheckUdp(14550); CheckUdp(14551); CheckUdp(14552); CheckUdp(14553);
                CheckTcp("127.0.0.1", 5760); CheckTcp("127.0.0.1", 5762); CheckTcp("127.0.0.1", 5763);
                CheckSerialPorts();

                await Task.Delay(3000, token);
            }
        }

        private void CheckUdp(int port)
        {
            lock (_lock)
            {
                string name = $"UDP:{port}";
                if (ConnectedDevices.ContainsKey(name)) return;
                if (_probingInterfaces.Any(i => i is UdpInterface u && u.Name.Contains(port.ToString()))) return;
                try { SetupProbe(new UdpInterface(port)); } catch { }
            }
        }

        private void CheckTcp(string host, int port)
        {
            lock (_lock)
            {
                string name = $"TCP:{host}:{port}";
                if (ConnectedDevices.ContainsKey(name)) return;
                if (_probingInterfaces.Any(i => i is TcpInterface t && t.Name.Contains(port.ToString()))) return;
                try { SetupProbe(new TcpInterface(host, port)); } catch { }
            }
        }

        private void CheckSerialPorts()
        {
            var ports = SerialPort.GetPortNames();
            lock (_lock)
            {
                foreach (var port in ports)
                {
                    foreach (var baud in new[] { 115200, 57600 })
                    {
                        string name = $"{port}@{baud}";
                        if (ConnectedDevices.ContainsKey(name) || _probingInterfaces.Any(i => i.Name == name)) continue;
                        try { SetupProbe(new SerialInterface(port, baud)); } catch { }
                    }
                }
            }
        }

        private void SetupProbe(MavLinkInterface iface)
        {
            _probingInterfaces.Add(iface);
            var parser = new MavLinkParser();
            parser.PacketReceived += (pkt) => {
                if (pkt.MessageId == MavLinkMessages.HEARTBEAT_ID) HandleDiscovery(iface, pkt);
            };
            iface.StartReading(data => parser.Parse(data));
        }

        private void HandleDiscovery(MavLinkInterface iface, MavLinkPacket pkt)
        {
            if (ConnectedDevices.TryGetValue(iface.Name, out var device))
            {
                device.LastHeartbeat = DateTime.Now;
                return;
            }

            lock (_lock)
            {
                _probingInterfaces.Remove(iface);
                var newDevice = new DiscoveredDevice { 
                    Interface = iface, SysId = pkt.SystemId, CompId = pkt.ComponentId, LastHeartbeat = DateTime.Now 
                };
                if (ConnectedDevices.TryAdd(iface.Name, newDevice))
                {
                    OnDeviceConnected?.Invoke(newDevice);
                }
            }
        }
    }
}
