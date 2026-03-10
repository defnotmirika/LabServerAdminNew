using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace LabServerAdmin.Services
{
    /// <summary>
    /// Sends Wake-on-LAN "magic packets" to wake sleeping/off PCs over the network.
    ///
    /// REQUIREMENTS (must be done on each student PC before WOL will work):
    ///   1. BIOS: Enable "Wake on LAN" / "PCI-E Wake" / "Resume by PCI-E"
    ///   2. Windows Device Manager → Network Adapter → Properties
    ///              → Power Management tab
    ///              → ✅ "Allow this device to wake the computer"
    ///              → ✅ "Only allow a magic packet to wake the computer"
    ///   3. Windows Settings → System → Power & Sleep → Additional power settings
    ///              → Choose what closing the lid does
    ///              → Turn on fast startup = DISABLED (can block WOL)
    ///
    /// appsettings.json example:
    /// "WakeOnLan": {
    ///   "BroadcastAddress": "192.168.1.255",   ← your subnet broadcast (optional, defaults to 255.255.255.255)
    ///   "Port": 9,                              ← standard WOL port (optional, default 9)
    ///   "MacAddresses": {
    ///     "PC-01": "AA:BB:CC:DD:EE:01",
    ///     "PC-02": "AA:BB:CC:DD:EE:02",
    ///     "PC-03": "AA:BB:CC:DD:EE:03"
    ///   }
    /// }
    /// </summary>
    public class WakeOnLanService
    {
        // MAC address lookup: PC name → MAC
        private readonly Dictionary<string, string> _macAddresses;
        private readonly IPAddress _broadcastAddress;
        private readonly int _port;

        public WakeOnLanService(IConfiguration configuration)
        {
            _macAddresses = configuration
                .GetSection("WakeOnLan:MacAddresses")
                .Get<Dictionary<string, string>>()?
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var broadcastSetting = configuration["WakeOnLan:BroadcastAddress"];
            _broadcastAddress = !string.IsNullOrWhiteSpace(broadcastSetting)
                && IPAddress.TryParse(broadcastSetting, out var parsed)
                ? parsed
                : IPAddress.Broadcast; // 255.255.255.255

            _port = int.TryParse(configuration["WakeOnLan:Port"], out var port) ? port : 9;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Wake a specific PC by its name (e.g. "PC-02").</summary>
        /// <returns>True if MAC was found and packet was sent; false if PC not configured.</returns>
        public bool WakePC(string pcName)
        {
            // Exact match first
            if (_macAddresses.TryGetValue(pcName, out var mac))
            {
                SendMagicPacket(mac);
                return true;
            }

            // Fuzzy match by number (e.g. "PC-2" matches "PC-02")
            var targetNum = System.Text.RegularExpressions.Regex
                .Match(pcName, @"\d+").Value.TrimStart('0');

            var match = _macAddresses.Keys.FirstOrDefault(k =>
                System.Text.RegularExpressions.Regex
                    .Match(k, @"\d+").Value.TrimStart('0') == targetNum);

            if (match != null)
            {
                SendMagicPacket(_macAddresses[match]);
                return true;
            }

            return false; // PC not in config
        }

        /// <summary>Wake ALL configured PCs at once.</summary>
        public void WakeAllPCs()
        {
            foreach (var mac in _macAddresses.Values)
                SendMagicPacket(mac);
        }

        /// <summary>Returns list of PC names that have a MAC address configured.</summary>
        public IReadOnlyList<string> GetConfiguredPCs() =>
            _macAddresses.Keys.OrderBy(k => k).ToList();

        // ── Magic Packet Builder ──────────────────────────────────────────────

        /// <summary>
        /// A WOL magic packet is:
        ///   - 6 bytes of 0xFF
        ///   - The target MAC address repeated 16 times
        /// Total: 102 bytes, sent via UDP broadcast.
        /// </summary>
        private void SendMagicPacket(string macAddress)
        {
            // Parse MAC — supports both "AA:BB:CC:DD:EE:FF" and "AA-BB-CC-DD-EE-FF"
            var macBytes = macAddress
                .Split(':', '-')
                .Select(hex => Convert.ToByte(hex, 16))
                .ToArray();

            if (macBytes.Length != 6)
                throw new ArgumentException($"Invalid MAC address: {macAddress}");

            // Build the 102-byte magic packet
            var packet = new byte[102];

            // First 6 bytes: 0xFF 0xFF 0xFF 0xFF 0xFF 0xFF
            for (int i = 0; i < 6; i++)
                packet[i] = 0xFF;

            // Remaining 96 bytes: MAC repeated 16 times
            for (int repetition = 0; repetition < 16; repetition++)
                for (int byteIndex = 0; byteIndex < 6; byteIndex++)
                    packet[6 + (repetition * 6) + byteIndex] = macBytes[byteIndex];

            // Send via UDP broadcast
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Send(packet, packet.Length, new IPEndPoint(_broadcastAddress, _port));
        }
    }
}