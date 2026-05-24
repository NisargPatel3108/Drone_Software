using System;
using System.IO;
using MinimalGCS.Mavlink;

namespace MinimalGCS.Mavlink
{
    public static class MavLinkCommands
    {
        public static byte[] CreateCommandLong(byte sysId, byte compId, byte targetSys, byte targetComp, ushort command, float p1, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0)
        {
            byte[] payload = new byte[33];
            // Format: float p1-p7 (28 bytes), ushort command (2 bytes), byte target_sys (1 byte), byte target_comp (1 byte), byte confirmation (1 byte)
            
            Buffer.BlockCopy(BitConverter.GetBytes(p1), 0, payload, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p2), 0, payload, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p3), 0, payload, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p4), 0, payload, 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p5), 0, payload, 16, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p6), 0, payload, 20, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p7), 0, payload, 24, 4);
            
            payload[28] = (byte)(command & 0xFF);
            payload[29] = (byte)((command >> 8) & 0xFF);
            
            payload[30] = targetSys;
            payload[31] = targetComp;
            payload[32] = 0; // confirmation

            return BuildPacket(sysId, compId, MavLinkMessages.COMMAND_LONG_ID, payload);
        }

        public static byte[] CreateSetMessageInterval(byte sysId, byte compId, byte targetSys, uint msgId, int intervalUs)
        {
            return CreateCommandLong(sysId, compId, targetSys, 1, 511, msgId, intervalUs);
        }

        public static byte[] CreateRequestDataStream(byte sysId, byte compId, byte targetSys, byte streamId, ushort rate, byte startStop)
        {
            byte[] payload = new byte[6];
            payload[0] = (byte)(rate & 0xFF);
            payload[1] = (byte)((rate >> 8) & 0xFF);
            payload[2] = targetSys;
            payload[3] = 1; // target component
            payload[4] = streamId;
            payload[5] = startStop;

            return BuildPacket(sysId, compId, 66, payload);
        }

        public static byte[] CreateSetMode(byte sysId, byte compId, byte targetSys, byte baseMode, uint customMode)
        {
            byte[] payload = new byte[6];
            Buffer.BlockCopy(BitConverter.GetBytes(customMode), 0, payload, 0, 4);
            payload[4] = targetSys;
            payload[5] = baseMode;

            return BuildPacket(sysId, compId, MavLinkMessages.SET_MODE_ID, payload);
        }

        public static byte[] CreateMissionCount(byte sysId, byte compId, byte targetSys, byte targetComp, ushort count)
        {
            byte[] payload = new byte[4];
            payload[0] = (byte)(count & 0xFF);
            payload[1] = (byte)((count >> 8) & 0xFF);
            payload[2] = targetSys;
            payload[3] = targetComp;
            return BuildPacket(sysId, compId, 44, payload);
        }

        public static byte[] CreateMissionItem(byte sysId, byte compId, byte targetSys, byte targetComp, ushort seq, ushort command, float p1, float p2, float p3, float p4, float lat, float lon, float alt, byte frame, byte current, byte autocontinue)
        {
            byte[] payload = new byte[37];
            Buffer.BlockCopy(BitConverter.GetBytes(p1), 0, payload, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p2), 0, payload, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p3), 0, payload, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p4), 0, payload, 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(lat), 0, payload, 16, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(lon), 0, payload, 20, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(alt), 0, payload, 24, 4);
            
            payload[28] = (byte)(seq & 0xFF);
            payload[29] = (byte)((seq >> 8) & 0xFF);
            
            payload[30] = (byte)(command & 0xFF);
            payload[31] = (byte)((command >> 8) & 0xFF);
            
            payload[32] = targetSys;
            payload[33] = targetComp;
            payload[34] = frame;
            payload[35] = current;
            payload[36] = autocontinue;
            
            return BuildPacket(sysId, compId, 39, payload);
        }

        private static byte _seq = 0;
        private static byte[] BuildPacket(byte sysId, byte compId, uint msgId, byte[] payload)
        {
            // MAVLink v1 for simplicity in command sending
            int len = 6 + payload.Length + 2;
            byte[] packet = new byte[len];
            packet[0] = 0xFE;
            packet[1] = (byte)payload.Length;
            packet[2] = _seq++; // Sequence
            packet[3] = sysId;
            packet[4] = compId;
            packet[5] = (byte)msgId;
            
            Buffer.BlockCopy(payload, 0, packet, 6, payload.Length);
            
            // CRC calculation
            byte[] forCrc = new byte[5 + payload.Length];
            Buffer.BlockCopy(packet, 1, forCrc, 0, 5 + payload.Length);
            
            byte crcExtra = MavLinkMessages.CrcExtras.ContainsKey(msgId) ? MavLinkMessages.CrcExtras[msgId] : (byte)0;
            ushort crc = MavLinkPacket.CalculateChecksum(forCrc, crcExtra);
            
            packet[len - 2] = (byte)(crc & 0xFF);
            packet[len - 1] = (byte)((crc >> 8) & 0xFF);
            
            return packet;
        }

        public static byte[] CreateParamRequestRead(byte sysId, byte compId, byte targetSys, byte targetComp, string paramId)
        {
            byte[] payload = new byte[20];
            // int16 param_index = -1
            payload[0] = 0xFF;
            payload[1] = 0xFF;
            payload[2] = targetSys;
            payload[3] = targetComp;
            
            byte[] idBytes = System.Text.Encoding.ASCII.GetBytes(paramId);
            int copyLen = Math.Min(idBytes.Length, 16);
            Buffer.BlockCopy(idBytes, 0, payload, 4, copyLen);
            
            return BuildPacket(sysId, compId, 20, payload);
        }

        public static byte[] CreateParamSet(byte sysId, byte compId, byte targetSys, byte targetComp, string paramId, float val, byte paramType)
        {
            byte[] payload = new byte[23];
            Buffer.BlockCopy(BitConverter.GetBytes(val), 0, payload, 0, 4);
            payload[4] = targetSys;
            payload[5] = targetComp;
            
            byte[] idBytes = System.Text.Encoding.ASCII.GetBytes(paramId);
            int copyLen = Math.Min(idBytes.Length, 16);
            Buffer.BlockCopy(idBytes, 0, payload, 6, copyLen);
            
            payload[22] = paramType; // e.g. 9 for REAL32
            
            return BuildPacket(sysId, compId, 23, payload);
        }
    }
}
