using System;
using System.Collections.Generic;

namespace MinimalGCS.Mavlink
{
    public class MavLinkPacket
    {
        public byte Magic { get; set; }
        public byte PayloadLength { get; set; }
        public byte IncompatFlags { get; set; }
        public byte CompatFlags { get; set; }
        public byte Sequence { get; set; }
        public byte SystemId { get; set; }
        public byte ComponentId { get; set; }
        public uint MessageId { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public ushort Checksum { get; set; }

        public bool IsV2 => Magic == 0xFD;

        public static ushort CalculateChecksum(byte[] data, byte crcExtra)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc = AccumulateChecksum(b, crc);
            }
            crc = AccumulateChecksum(crcExtra, crc);
            return crc;
        }

        private static ushort AccumulateChecksum(byte data, ushort crc)
        {
            unchecked
            {
                byte tmp = (byte)(data ^ (byte)(crc & 0xff));
                tmp ^= (byte)(tmp << 4);
                return (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
            }
        }
    }

    public static class MavLinkMessages
    {
        public const byte HEARTBEAT_ID = 0;
        public const byte COMMAND_LONG_ID = 76;
        public const byte SET_MODE_ID = 11;

        // CRC extras for common messages (ArduPilot/PX4 standard)
        public static readonly Dictionary<uint, byte> CrcExtras = new Dictionary<uint, byte>
        {
            { 0, 50 },    // HEARTBEAT
            { 76, 152 },  // COMMAND_LONG
            { 11, 89 },   // SET_MODE
            { 1, 124 },   // SYS_STATUS
            { 24, 24 },   // GPS_RAW_INT
            { 33, 104 },  // GLOBAL_POSITION_INT (ArduPilot Standard)
            { 253, 83 }   // STATUSTEXT
        };
    }
}
