using System;
using System.IO.Ports;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
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

        public override string ToString() => $"Drone {SysId} ({Interface.Name})";
    }

    public class AutoConnector
    {
        private readonly List<MavLinkInterface> _probingInterfaces = new List<MavLinkInterface>();
        public readonly ConcurrentDictionary<string, DiscoveredDevice> ConnectedDevices = new ConcurrentDictionary<string, DiscoveredDevice>();
        private readonly object _lock = new object();
        
        private readonly Dictionary<string, int> _lastBaudIndex = new Dictionary<string, int>();
        private readonly int[] _bauds = new[] { 57600, 115200, 921600, 38400, 9600 };

        public event Action<DiscoveredDevice>? OnDeviceConnected;
        public event Action<string>? OnDeviceDisconnected;

        private CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _isRunning = false;

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            StartBackend();
            Task.Run(() => ScanningLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts.Cancel();
            _isRunning = false;
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
            // Removed MAVProxy auto-start to prevent COM port locking.
            // Application will directly connect via Serial, TCP, or UDP. 
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

                var tasks = new List<Task>();
                
                // 1. FAST PROBE: Standard GCS Relay / Mirrored Ports (Industry standard 14550/14551)
                tasks.Add(Task.Run(() => CheckUdp(14550)));
                tasks.Add(Task.Run(() => CheckUdp(14551)));
                tasks.Add(Task.Run(() => CheckUdp(14552)));

                // 2. TCP PROBE (SITL/Bridge)
                tasks.Add(Task.Run(async () => await CheckTcpAsync("127.0.0.1", 5760))); 
                tasks.Add(Task.Run(async () => await CheckTcpAsync("127.0.0.1", 5762))); 
                
                // 3. SERIAL PROBE (Direct Hardware COM ports)
                tasks.Add(Task.Run(() => CheckSerialPorts()));

                await Task.WhenAll(tasks);
                await Task.Delay(1500, token); // Balanced scanning frequency
            }
        }

        private void CheckUdp(int port)
        {
            lock (_lock)
            {
                string name = $"UDP:{port}";
                if (ConnectedDevices.ContainsKey(name) || _probingInterfaces.Any(i => i.Name == name)) return;
                try { SetupProbe(new UdpInterface(port)); } catch { }
            }
        }

        private async Task CheckTcpAsync(string host, int port)
        {
            string name = $"TCP:{host}:{port}";
            lock (_lock) { if (ConnectedDevices.ContainsKey(name) || _probingInterfaces.Any(i => i.Name == name)) return; }

            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(host, port);
                    if (await Task.WhenAny(connectTask, Task.Delay(1000)) == connectTask) // 1000ms for more reliability
                    {
                        if (client.Connected)
                        {
                            var iface = new TcpInterface(host, port); // New instance to keep open
                            lock (_lock) { SetupProbe(iface); }
                        }
                    }
                }
            }
            catch { }
        }

        private void CheckSerialPorts()
        {
            var ports = SerialPort.GetPortNames();
            lock (_lock)
            {
                foreach (var port in ports)
                {
                    // If this port is already connected or currently probing, skip it
                    if (ConnectedDevices.Keys.Any(k => k.StartsWith(port + "@")) || 
                        _probingInterfaces.Any(i => i.Name.StartsWith(port + "@")))
                    {
                        continue;
                    }

                    // Rotate to the next baud rate to try for this port
                    if (!_lastBaudIndex.TryGetValue(port, out int index))
                    {
                        index = 0;
                    }
                    else
                    {
                        index = (index + 1) % _bauds.Length;
                    }
                    _lastBaudIndex[port] = index;

                    int baud = _bauds[index];
                    try 
                    { 
                        SetupProbe(new SerialInterface(port, baud)); 
                    } 
                    catch { }
                }
            }
        }

        private void SetupProbe(MavLinkInterface iface)
        {
            lock (_lock)
            {
                _probingInterfaces.Add(iface);
            }
            var parser = new MavLinkParser();
            parser.PacketReceived += (pkt) => {
                if (pkt.MessageId == MavLinkMessages.HEARTBEAT_ID) HandleDiscovery(iface, pkt);
            };
            iface.OnDataReceived += data => parser.Parse(data);
            iface.StartReading();

            // Auto-cleanup probe after 5 seconds if not discovered to free up ports/resources (except UDP)
            if (!(iface is UdpInterface))
            {
                Task.Run(async () => {
                    await Task.Delay(5000);
                    lock (_lock)
                    {
                        if (_probingInterfaces.Contains(iface))
                        {
                            _probingInterfaces.Remove(iface);
                            iface.Close();
                        }
                    }
                });
            }
        }

        private void HandleDiscovery(MavLinkInterface iface, MavLinkPacket pkt)
        {
            // Allow connecting to GCS/Mission Planner (SystemId 255, Heartbeat Type 6) when in UDP mode
            if (!(iface is UdpInterface))
            {
                // PRO-LEVEL FILTERING:
                // Heartbeat Type 6 = GCS. We ONLY want drones (Type 1-5, 10+, etc.)
                if (pkt.Payload.Length > 4 && pkt.Payload[4] == 6) return;
                
                // Skip packets from standard GCS IDs (255) to avoid connecting to ourselves or MAVProxy
                if (pkt.SystemId == 255) return;
            }

            // Prevent loopback discovery: do not discover on UDP if already directly connected via Serial
            if (iface is UdpInterface && ConnectedDevices.Values.Any(d => d.SysId == pkt.SystemId && d.Interface is SerialInterface)) return;

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
