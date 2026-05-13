using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace OSMImporter.Traffic.Sumo
{
    /// <summary>
    /// TraCI TCP client — giao tiếp với SUMO qua TraCI protocol.
    /// Gửi lệnh nhị phân, nhận response nhị phân, parse kết quả.
    /// Ref: https://sumo.dlr.de/docs/TraCI/Protocol.html
    /// </summary>
    public class TraCIClient : IDisposable
    {
        // TraCI command IDs
        private const byte CMD_SIMSTEP          = 0x02;
        private const byte CMD_CLOSE            = 0x7F;
        private const byte CMD_GET_VEHICLE_VAR  = 0xA4;
        private const byte CMD_GET_SIM_VAR      = 0xAB;
        private const byte CMD_GET_TL_VAR       = 0xA2;
        private const byte CMD_SUBSCRIBE_VEHICLE_VAR = 0xD4;
        private const byte CMD_RESPONSE_SUBSCRIBE_VEHICLE_VAR = 0xE4;

        // TraCI variable IDs
        private const byte VAR_ID_LIST          = 0x00;
        private const byte VAR_POSITION         = 0x42;
        private const byte VAR_ANGLE            = 0x43;
        private const byte VAR_SPEED            = 0x40;
        private const byte VAR_TYPE             = 0x4F;
        private const byte VAR_LENGTH           = 0x44;
        private const byte VAR_WIDTH            = 0x4D;
        private const byte VAR_ROAD_ID          = 0x50;
        private const byte VAR_LANE_INDEX       = 0x52;

        // TLS-specific variable IDs
        private const byte TL_RED_YELLOW_GREEN_STATE = 0x20;
        private const byte TL_CURRENT_PHASE    = 0x28;
        private const byte TL_CURRENT_PROGRAM  = 0x29;
        private const byte TL_PHASE_DURATION   = 0x24;

        // TraCI type IDs
        private const byte TYPE_INTEGER         = 0x09;
        private const byte TYPE_DOUBLE          = 0x0B;
        private const byte TYPE_STRING          = 0x0C;
        private const byte TYPE_STRINGLIST      = 0x0E;
        private const byte TYPE_POSITION2D      = 0x01;

        // Subscription state
        private bool _vehicleSubActive;

        private TcpClient _tcp;
        private NetworkStream _stream;
        private readonly string _host;
        private readonly int _port;

        public bool IsConnected => _tcp != null && _tcp.Connected;

        public TraCIClient(string host = "127.0.0.1", int port = 8813)
        {
            _host = host;
            _port = port;
        }

        // ══════════════════════════════════════════════════════════════════
        // CONNECTION
        // ══════════════════════════════════════════════════════════════════

        public bool Connect(int timeoutMs = 5000)
        {
            try
            {
                _tcp = new TcpClient();
                _tcp.SendTimeout = timeoutMs;
                _tcp.ReceiveTimeout = timeoutMs;
                _tcp.NoDelay = true;

                var result = _tcp.BeginConnect(_host, _port, null, null);
                bool connected = result.AsyncWaitHandle.WaitOne(timeoutMs);
                if (!connected || !_tcp.Connected)
                {
                    Debug.LogError($"[TraCI] Connection timeout → {_host}:{_port}");
                    _tcp.Close();
                    _tcp = null;
                    return false;
                }
                _tcp.EndConnect(result);
                _stream = _tcp.GetStream();
                Debug.Log($"[TraCI] Connected → {_host}:{_port}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[TraCI] Connection failed: {e.Message}");
                _tcp = null;
                return false;
            }
        }

        public void Close()
        {
            if (!IsConnected) return;
            try
            {
                SendCommand(CMD_CLOSE);
                _stream?.Close();
                _tcp?.Close();
            }
            catch { }
            _stream = null;
            _tcp = null;
            Debug.Log("[TraCI] Disconnected");
        }

        public void Dispose() => Close();

        // ══════════════════════════════════════════════════════════════════
        // SIMULATION
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Tiến 1 bước mô phỏng SUMO.</summary>
        public void SimulationStep(double targetTime = 0.0)
        {
            // CMD_SIMSTEP + targetTime (double)
            var cmd = new List<byte> { CMD_SIMSTEP };
            WriteDouble(cmd, targetTime);
            SendCommand(cmd);
            var resp = ReceiveResponse();

            // Nếu đã subscribe, parse subscription results từ SimStep response
            if (_vehicleSubActive && resp != null)
                ParseSubscriptionResults(resp);
        }

        // ══════════════════════════════════════════════════════════════════
        // VEHICLE QUERIES
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Danh sách ID xe đang tồn tại trong simulation.</summary>
        public List<string> GetVehicleIDList()
        {
            return GetStringListVariable(CMD_GET_VEHICLE_VAR, VAR_ID_LIST, "");
        }

        /// <summary>Vị trí SUMO (x, y) = meters từ gốc network.</summary>
        public Vector2 GetVehiclePosition(string vehicleId)
        {
            var response = GetVariable(CMD_GET_VEHICLE_VAR, VAR_POSITION, vehicleId);
            if (response == null || response.Length < 17) return Vector2.zero;

            int offset = FindResponseData(response);
            if (offset < 0 || response[offset] != TYPE_POSITION2D) return Vector2.zero;
            offset++;

            double x = ReadDouble(response, offset); offset += 8;
            double y = ReadDouble(response, offset);
            return new Vector2((float)x, (float)y);
        }

        /// <summary>Góc SUMO: 0=North, clockwise, degrees.</summary>
        public float GetVehicleAngle(string vehicleId)
        {
            return (float)GetDoubleVariable(CMD_GET_VEHICLE_VAR, VAR_ANGLE, vehicleId);
        }

        /// <summary>Tốc độ m/s.</summary>
        public float GetVehicleSpeed(string vehicleId)
        {
            return (float)GetDoubleVariable(CMD_GET_VEHICLE_VAR, VAR_SPEED, vehicleId);
        }

        /// <summary>Loại xe SUMO (string).</summary>
        public string GetVehicleType(string vehicleId)
        {
            return GetStringVariable(CMD_GET_VEHICLE_VAR, VAR_TYPE, vehicleId);
        }

        /// <summary>Chiều dài xe (m).</summary>
        public float GetVehicleLength(string vehicleId)
        {
            return (float)GetDoubleVariable(CMD_GET_VEHICLE_VAR, VAR_LENGTH, vehicleId);
        }

        /// <summary>Chiều rộng xe (m).</summary>
        public float GetVehicleWidth(string vehicleId)
        {
            return (float)GetDoubleVariable(CMD_GET_VEHICLE_VAR, VAR_WIDTH, vehicleId);
        }

        /// <summary>Edge ID hiện tại.</summary>
        public string GetVehicleRoadId(string vehicleId)
        {
            return GetStringVariable(CMD_GET_VEHICLE_VAR, VAR_ROAD_ID, vehicleId);
        }

        /// <summary>Lane index hiện tại (0-based).</summary>
        public int GetVehicleLaneIndex(string vehicleId)
        {
            return GetIntVariable(CMD_GET_VEHICLE_VAR, VAR_LANE_INDEX, vehicleId);
        }

        // ══════════════════════════════════════════════════════════════════
        // TRAFFIC LIGHT QUERIES
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Danh sách Traffic Light IDs.</summary>
        public List<string> GetTLSIdList()
        {
            return GetStringListVariable(CMD_GET_TL_VAR, VAR_ID_LIST, "");
        }

        /// <summary>
        /// Lấy trạng thái đèn: chuỗi ký tự "rRgGyYoOsS" cho từng link.
        /// r/R=red, g/G=green, y/Y=yellow, o/O=off, s/S=unused
        /// </summary>
        public string GetTLSState(string tlsId)
        {
            return GetStringVariable(CMD_GET_TL_VAR, TL_RED_YELLOW_GREEN_STATE, tlsId);
        }

        /// <summary>Phase index hiện tại (0-based).</summary>
        public int GetTLSCurrentPhase(string tlsId)
        {
            return GetIntVariable(CMD_GET_TL_VAR, TL_CURRENT_PHASE, tlsId);
        }

        /// <summary>Thời gian còn lại của phase hiện tại (giây).</summary>
        public float GetTLSPhaseDuration(string tlsId)
        {
            return (float)GetDoubleVariable(CMD_GET_TL_VAR, TL_PHASE_DURATION, tlsId);
        }

        /// <summary>
        /// Struct chứa trạng thái 1 traffic light.
        /// </summary>
        public struct TLSState
        {
            public string Id;
            public string State;      // "rRgGyY..." string
            public int PhaseIndex;
            public float PhaseDuration;
        }

        /// <summary>Lấy toàn bộ TLS states.</summary>
        public List<TLSState> GetAllTLSStates()
        {
            var ids = GetTLSIdList();
            var result = new List<TLSState>(ids.Count);
            foreach (var id in ids)
            {
                result.Add(new TLSState
                {
                    Id = id,
                    State = GetTLSState(id),
                    PhaseIndex = GetTLSCurrentPhase(id),
                    PhaseDuration = GetTLSPhaseDuration(id)
                });
            }
            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        // SUBSCRIPTION — push data từ SUMO, 0 round-trip per frame
        // ══════════════════════════════════════════════════════════════════

        public struct VehicleState
        {
            public string Id;
            public Vector2 Position;   // SUMO meters
            public float Angle;        // degrees
            public float Speed;        // m/s
            public string VehicleType;
        }

        // Cached subscription results — cập nhật mỗi SimStep
        private List<VehicleState> _subscriptionCache = new List<VehicleState>();

        /// <summary>
        /// Subscribe vehicle variables 1 lần. SUMO sẽ tự push data sau mỗi SimStep.
        /// Gọi 1 lần sau khi connect, không cần gọi lại.
        /// </summary>
        public bool SubscribeVehicleVariables()
        {
            try
            {
                // Subscribe cho tất cả xe (objectId = "") với begin=0, end=max
                var cmd = new List<byte> { CMD_SUBSCRIBE_VEHICLE_VAR };

                // Begin time (double) = 0
                WriteDouble(cmd, 0.0);
                // End time (double) = rất lớn
                WriteDouble(cmd, 1e12);

                // Object ID = "" (subscribe cho tất cả)
                WriteString(cmd, "");

                // Số biến cần subscribe
                cmd.Add(4);
                cmd.Add(VAR_POSITION);
                cmd.Add(VAR_ANGLE);
                cmd.Add(VAR_SPEED);
                cmd.Add(VAR_TYPE);

                SendCommand(cmd);
                var resp = ReceiveResponse();
                _vehicleSubActive = resp != null;

                if (_vehicleSubActive)
                    Debug.Log("[TraCI] Vehicle subscription active — zero round-trip mode.");
                else
                    Debug.LogWarning("[TraCI] Vehicle subscription failed — falling back to polling.");

                return _vehicleSubActive;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TraCI] Subscription error: {e.Message} — using polling fallback.");
                _vehicleSubActive = false;
                return false;
            }
        }

        /// <summary>
        /// Parse subscription results từ SimStep response.
        /// SUMO gửi kèm subscription data sau mỗi simulation step.
        /// </summary>
        private void ParseSubscriptionResults(byte[] resp)
        {
            _subscriptionCache.Clear();
            if (resp == null || resp.Length < 5) return;

            try
            {
                int offset = 0;
                while (offset < resp.Length - 4)
                {
                    // Mỗi subscription response: [len][cmdId=0xE4][objectId][numVars][var+type+data...]
                    int cmdLen = resp[offset];
                    if (cmdLen == 0 && offset + 5 <= resp.Length)
                    {
                        cmdLen = ReadInt(resp, offset + 1);
                        offset += 5;
                    }
                    else
                    {
                        offset++;
                    }

                    if (offset >= resp.Length) break;
                    byte cmdId = resp[offset++];

                    // Skip non-subscription responses (status, etc)
                    if (cmdId != CMD_RESPONSE_SUBSCRIBE_VEHICLE_VAR)
                    {
                        offset += cmdLen - 2;
                        continue;
                    }

                    // Parse object ID
                    if (offset + 4 > resp.Length) break;
                    int idLen = ReadInt(resp, offset); offset += 4;
                    if (offset + idLen > resp.Length) break;
                    string vehicleId = Encoding.ASCII.GetString(resp, offset, idLen);
                    offset += idLen;

                    if (offset >= resp.Length) break;
                    int numVars = resp[offset++];

                    var state = new VehicleState { Id = vehicleId };

                    // Parse từng variable
                    for (int v = 0; v < numVars && offset < resp.Length; v++)
                    {
                        if (offset + 2 > resp.Length) break;
                        byte varId = resp[offset++];
                        byte status = resp[offset++]; // 0x00 = OK

                        if (status != 0x00)
                        {
                            // Skip error data
                            if (offset + 1 <= resp.Length)
                            {
                                byte errType = resp[offset++];
                                if (errType == TYPE_STRING && offset + 4 <= resp.Length)
                                {
                                    int errLen = ReadInt(resp, offset); offset += 4;
                                    offset += errLen;
                                }
                            }
                            continue;
                        }

                        if (offset >= resp.Length) break;
                        byte dataType = resp[offset++];

                        switch (varId)
                        {
                            case VAR_POSITION:
                                if (dataType == TYPE_POSITION2D && offset + 16 <= resp.Length)
                                {
                                    double x = ReadDouble(resp, offset); offset += 8;
                                    double y = ReadDouble(resp, offset); offset += 8;
                                    state.Position = new Vector2((float)x, (float)y);
                                }
                                break;
                            case VAR_ANGLE:
                                if (dataType == TYPE_DOUBLE && offset + 8 <= resp.Length)
                                {
                                    state.Angle = (float)ReadDouble(resp, offset); offset += 8;
                                }
                                break;
                            case VAR_SPEED:
                                if (dataType == TYPE_DOUBLE && offset + 8 <= resp.Length)
                                {
                                    state.Speed = (float)ReadDouble(resp, offset); offset += 8;
                                }
                                break;
                            case VAR_TYPE:
                                if (dataType == TYPE_STRING && offset + 4 <= resp.Length)
                                {
                                    int sLen = ReadInt(resp, offset); offset += 4;
                                    if (offset + sLen <= resp.Length)
                                    {
                                        state.VehicleType = Encoding.ASCII.GetString(resp, offset, sLen);
                                        offset += sLen;
                                    }
                                }
                                break;
                        }
                    }

                    _subscriptionCache.Add(state);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TraCI] Subscription parse error: {e.Message}");
            }
        }

        /// <summary>
        /// Lấy toàn bộ state xe.
        /// Nếu subscription active → trả về cached data (0 round-trip).
        /// Nếu không → fallback polling (N×4 round-trips).
        /// </summary>
        public List<VehicleState> GetAllVehicleStates()
        {
            // Subscription mode: data đã được parse trong SimulationStep
            if (_vehicleSubActive && _subscriptionCache.Count > 0)
                return _subscriptionCache;

            // Fallback: polling mode
            var ids = GetVehicleIDList();
            var states = new List<VehicleState>(ids.Count);

            foreach (var id in ids)
            {
                states.Add(new VehicleState
                {
                    Id = id,
                    Position = GetVehiclePosition(id),
                    Angle = GetVehicleAngle(id),
                    Speed = GetVehicleSpeed(id),
                    VehicleType = GetVehicleType(id)
                });
            }
            return states;
        }

        // ══════════════════════════════════════════════════════════════════
        // INTERNAL — TraCI binary protocol
        // ══════════════════════════════════════════════════════════════════

        private byte[] GetVariable(byte cmdId, byte varId, string objectId)
        {
            var cmd = new List<byte> { cmdId, varId };
            WriteString(cmd, objectId);
            SendCommand(cmd);
            return ReceiveResponse();
        }

        private double GetDoubleVariable(byte cmdId, byte varId, string objectId)
        {
            byte[] resp = GetVariable(cmdId, varId, objectId);
            if (resp == null) return 0.0;
            int offset = FindResponseData(resp);
            if (offset < 0 || resp[offset] != TYPE_DOUBLE) return 0.0;
            return ReadDouble(resp, offset + 1);
        }

        private int GetIntVariable(byte cmdId, byte varId, string objectId)
        {
            byte[] resp = GetVariable(cmdId, varId, objectId);
            if (resp == null) return 0;
            int offset = FindResponseData(resp);
            if (offset < 0 || resp[offset] != TYPE_INTEGER) return 0;
            return ReadInt(resp, offset + 1);
        }

        private string GetStringVariable(byte cmdId, byte varId, string objectId)
        {
            byte[] resp = GetVariable(cmdId, varId, objectId);
            if (resp == null) return "";
            int offset = FindResponseData(resp);
            if (offset < 0 || resp[offset] != TYPE_STRING) return "";
            return ReadString(resp, offset + 1);
        }

        private List<string> GetStringListVariable(byte cmdId, byte varId, string objectId)
        {
            byte[] resp = GetVariable(cmdId, varId, objectId);
            var result = new List<string>();
            if (resp == null) return result;
            int offset = FindResponseData(resp);
            if (offset < 0 || resp[offset] != TYPE_STRINGLIST) return result;
            offset++;
            int count = ReadInt(resp, offset); offset += 4;
            for (int i = 0; i < count; i++)
            {
                int len = ReadInt(resp, offset); offset += 4;
                result.Add(Encoding.ASCII.GetString(resp, offset, len));
                offset += len;
            }
            return result;
        }

        // ── Send/Receive ──

        private void SendCommand(byte commandId)
        {
            SendCommand(new List<byte> { commandId });
        }

        private void SendCommand(List<byte> commandBody)
        {
            // Command: [length(1 or 5)] [body...]
            int cmdLen = 1 + commandBody.Count; // length byte + body
            bool extended = cmdLen > 255;

            var packet = new List<byte>();
            if (extended)
            {
                packet.Add(0); // marker for extended
                WriteInt(packet, cmdLen + 4); // 4 bytes for length
            }
            else
            {
                packet.Add((byte)cmdLen);
            }
            packet.AddRange(commandBody);

            // Message: [total_length(4)] [commands...]
            var message = new List<byte>();
            WriteInt(message, 4 + packet.Count);
            message.AddRange(packet);

            _stream.Write(message.ToArray(), 0, message.Count);
            _stream.Flush();
        }

        private byte[] ReceiveResponse()
        {
            // Đọc 4 byte tổng length
            byte[] lenBuf = ReadExact(4);
            if (lenBuf == null) return null;
            int totalLen = ReadInt(lenBuf, 0);

            // Đọc phần còn lại
            int remaining = totalLen - 4;
            if (remaining <= 0) return null;
            return ReadExact(remaining);
        }

        private byte[] ReadExact(int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = _stream.Read(buf, offset, count - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return buf;
        }

        // Tìm vị trí data trong response (skip status + response header)
        private int FindResponseData(byte[] resp)
        {
            if (resp == null || resp.Length < 2) return -1;
            int offset = 0;

            // Skip status response
            int statusLen = resp[offset];
            if (statusLen == 0 && resp.Length > 5)
            {
                statusLen = ReadInt(resp, offset + 1);
                offset += statusLen;
            }
            else
            {
                offset += statusLen;
            }

            if (offset >= resp.Length) return -1;

            // Response command: [len][cmdId][varId][objectId_string][type][data...]
            int respLen = resp[offset]; offset++;
            if (respLen == 0)
            {
                respLen = ReadInt(resp, offset); offset += 4;
            }
            offset++; // cmdId (response)
            offset++; // varId

            // Skip objectId string
            int strLen = ReadInt(resp, offset); offset += 4;
            offset += strLen;

            return offset; // now at [type][data...]
        }

        // ── Binary helpers (big-endian, TraCI convention) ──

        private static void WriteInt(List<byte> buf, int value)
        {
            buf.Add((byte)((value >> 24) & 0xFF));
            buf.Add((byte)((value >> 16) & 0xFF));
            buf.Add((byte)((value >> 8) & 0xFF));
            buf.Add((byte)(value & 0xFF));
        }

        private static void WriteDouble(List<byte> buf, double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            buf.AddRange(bytes);
        }

        private static void WriteString(List<byte> buf, string value)
        {
            byte[] strBytes = Encoding.ASCII.GetBytes(value ?? "");
            WriteInt(buf, strBytes.Length);
            buf.AddRange(strBytes);
        }

        private static int ReadInt(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16)
                 | (data[offset + 2] << 8) | data[offset + 3];
        }

        private static double ReadDouble(byte[] data, int offset)
        {
            byte[] bytes = new byte[8];
            Array.Copy(data, offset, bytes, 0, 8);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToDouble(bytes, 0);
        }

        private static string ReadString(byte[] data, int offset)
        {
            int len = ReadInt(data, offset);
            return Encoding.ASCII.GetString(data, offset + 4, len);
        }
    }
}
