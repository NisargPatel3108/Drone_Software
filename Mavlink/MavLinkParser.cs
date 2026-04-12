using System;
using System.Collections.Generic;

namespace MinimalGCS.Mavlink
{
    public class MavLinkParser
    {
        private enum ParseState
        {
            WaitStx,
            WaitLen,
            WaitIncompat,
            WaitCompat,
            WaitSeq,
            WaitSysId,
            WaitCompId,
            WaitMsgId1,
            WaitMsgId2,
            WaitMsgId3,
            WaitPayload,
            WaitCrc1,
            WaitCrc2
        }

        private ParseState _state = ParseState.WaitStx;
        private MavLinkPacket _currentPacket = new MavLinkPacket();
        private int _payloadCounter = 0;
        private List<byte> _rawForCrc = new List<byte>();

        public event Action<MavLinkPacket>? PacketReceived;

        public void Parse(byte[] data)
        {
            foreach (byte b in data)
            {
                ProcessByte(b);
            }
        }

        private void ProcessByte(byte b)
        {
            switch (_state)
            {
                case ParseState.WaitStx:
                    if (b == 0xFD || b == 0xFE)
                    {
                        _currentPacket = new MavLinkPacket { Magic = b };
                        _state = ParseState.WaitLen;
                        _rawForCrc.Clear();
                    }
                    break;

                case ParseState.WaitLen:
                    _currentPacket.PayloadLength = b;
                    _rawForCrc.Add(b);
                    if (_currentPacket.Magic == 0xFD)
                        _state = ParseState.WaitIncompat;
                    else
                        _state = ParseState.WaitSeq; // MAVLink v1
                    break;

                case ParseState.WaitIncompat:
                    _currentPacket.IncompatFlags = b;
                    _rawForCrc.Add(b);
                    _state = ParseState.WaitCompat;
                    break;

                case ParseState.WaitCompat:
                    _currentPacket.CompatFlags = b;
                    _rawForCrc.Add(b);
                    _state = ParseState.WaitSeq;
                    break;

                case ParseState.WaitSeq:
                    _currentPacket.Sequence = b;
                    _rawForCrc.Add(b);
                    _state = ParseState.WaitSysId;
                    break;

                case ParseState.WaitSysId:
                    _currentPacket.SystemId = b;
                    _rawForCrc.Add(b);
                    _state = ParseState.WaitCompId;
                    break;

                case ParseState.WaitCompId:
                    _currentPacket.ComponentId = b;
                    _rawForCrc.Add(b);
                    if (_currentPacket.Magic == 0xFD)
                        _state = ParseState.WaitMsgId1;
                    else
                        _state = ParseState.WaitMsgId1; // v1 msgid is 1 byte, but we handle via WaitMsgId1
                    break;

                case ParseState.WaitMsgId1:
                    _currentPacket.MessageId = b;
                    _rawForCrc.Add(b);
                    if (_currentPacket.Magic == 0xFD)
                        _state = ParseState.WaitMsgId2;
                    else
                        TransitionToPayload();
                    break;

                case ParseState.WaitMsgId2:
                    _currentPacket.MessageId |= (uint)(b << 8);
                    _rawForCrc.Add(b);
                    _state = ParseState.WaitMsgId3;
                    break;

                case ParseState.WaitMsgId3:
                    _currentPacket.MessageId |= (uint)(b << 16);
                    _rawForCrc.Add(b);
                    TransitionToPayload();
                    break;

                case ParseState.WaitPayload:
                    _currentPacket.Payload[_payloadCounter++] = b;
                    _rawForCrc.Add(b);
                    if (_payloadCounter >= _currentPacket.PayloadLength)
                        _state = ParseState.WaitCrc1;
                    break;

                case ParseState.WaitCrc1:
                    _currentPacket.Checksum = b;
                    _state = ParseState.WaitCrc2;
                    break;

                case ParseState.WaitCrc2:
                    _currentPacket.Checksum |= (ushort)(b << 8);
                    
                    // Validate CRC
                    if (MavLinkMessages.CrcExtras.TryGetValue(_currentPacket.MessageId, out byte crcExtra))
                    {
                        ushort calc = MavLinkPacket.CalculateChecksum(_rawForCrc.ToArray(), crcExtra);
                        if (calc == _currentPacket.Checksum)
                        {
                            PacketReceived?.Invoke(_currentPacket);
                        }
                    }
                    else
                    {
                        // Even if we don't know the CRC extra, for basic GCS we might just accept it 
                        // but it's safer to only accept known messages for control.
                        // For auto-connect, we definitely know HEARTBEAT (ID 0, extra 50).
                        if (_currentPacket.MessageId == 0) PacketReceived?.Invoke(_currentPacket);
                    }
                    _state = ParseState.WaitStx;
                    break;
            }
        }

        private void TransitionToPayload()
        {
            if (_currentPacket.PayloadLength > 0)
            {
                _currentPacket.Payload = new byte[_currentPacket.PayloadLength];
                _payloadCounter = 0;
                _state = ParseState.WaitPayload;
            }
            else
            {
                _state = ParseState.WaitCrc1;
            }
        }
    }
}
