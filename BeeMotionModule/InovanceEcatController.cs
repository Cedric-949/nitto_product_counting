using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeeMotionModule.Models;
using Inovance.InoMotionCotrollerShop.InoServiceContract.EtherCATConfigApi;

namespace BeeMotionModule
{
    public class InovanceEcatController : IMotionController
    {
        private ulong _cardHandle = 0;
        private MotionConfig _config = new MotionConfig();
        private readonly ConcurrentDictionary<short, AxisState> _axisStates = new ConcurrentDictionary<short, AxisState>();
        private Thread _pollingThread;
        private volatile bool _isPollingRunning = false;
        private readonly object _lockObj = new object();
        private bool _disposed = false;

        public bool IsConnected => _cardHandle != 0 || (_config != null && _config.Simulate);
        public bool IsMasterOp { get; private set; } = false;
        public uint MasterStatus { get; private set; } = 0;
        public MotionConfig Config => _config;

        public event Action<short, AxisState> OnAxisStateUpdated;
        public event Action<string> OnLogMessage;
        public event Action<uint, string> OnError;

        public InovanceEcatController()
        {
        }

        public bool Init(MotionConfig config)
        {
            lock (_lockObj)
            {
                _config = config ?? new MotionConfig();
                Log($"[Motion] Initializing Card ID: {_config.CardId}, Simulate: {_config.Simulate}...");

                // Initialize internal axis states
                foreach (var axisCfg in _config.Axes)
                {
                    _axisStates[axisCfg.AxisIndex] = new AxisState { AxisIndex = axisCfg.AxisIndex };
                }

                if (_config.Simulate)
                {
                    IsMasterOp = true;
                    MasterStatus = 6;
                    StartPollingThread();
                    Log("[Motion] Simulation Mode activated successfully.");
                    return true;
                }

                try
                {
                    int cardsNum = 0;
                    uint numRes = ImcApi.IMC_GetCardsNum(ref cardsNum);
                    Log($"[Motion] Query Inovance Card count: {cardsNum} (Result: 0x{numRes:X8}).");

                    uint res = ImcApi.IMC_OpenCard((int)_config.CardId, ref _cardHandle, 1);
                    if (res != ImcApi.EXE_SUCCESS)
                    {
                        string errMsg = $"Open Inovance Card ID {_config.CardId} failed (Code: 0x{res:X8}). Card may not be present or driver not loaded.";
                        Log($"[Motion ERROR] {errMsg}");
                        OnError?.Invoke(res, errMsg);
                        return false;
                    }

                    Log($"[Motion] Card opened successfully (Handle: 0x{_cardHandle:X16}).");

                    // Invert Emergency Stop trigger level if needed (tempt/Movi setting: 1 = inverted)
                    short emgInv = 0;
                    ImcApi.IMC_GetEmgTrigLevelInv(_cardHandle, ref emgInv);
                    if (emgInv != 1)
                    {
                        ImcApi.IMC_SetEmgTrigLevelInv(_cardHandle, 1);
                        Log("[Motion] Set EMG Trigger Level Inversion = 1 (Hardware safety bypass).");
                    }

                    // Check EtherCAT Master status
                    uint sts = 0;
                    ImcApi.IMC_GetECATMasterSts(_cardHandle, ref sts);
                    MasterStatus = sts;
                    IsMasterOp = (sts == 6);

                    if (!IsMasterOp)
                    {
                        Log($"[Motion] EtherCAT not in OP state (Status={sts}). Loading config files and scanning bus...");
                        ScanBus();
                    }
                    else
                    {
                        Log("[Motion] EtherCAT is in OP state (Code 6). Skipping XML config reload.");
                    }

                    // Bind configured axes
                    foreach (var axisCfg in _config.Axes)
                    {
                        short axType = -1, axChn = -1;
                        ImcApi.IMC_GetAxType(_cardHandle, axisCfg.AxisIndex, ref axType, ref axChn);
                        if (axType != 1) // 1 = EtherCAT
                        {
                            uint bondRes = ImcApi.IMC_SetAxBondCfg(_cardHandle, axisCfg.AxisIndex, 1, (short)axisCfg.AxisIndex, 0);
                            Log($"[Motion] Bind Axis {axisCfg.AxisIndex} to EtherCAT Channel {axisCfg.AxisIndex}: Result 0x{bondRes:X8}");
                        }
                    }

                    StartPollingThread();
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"[Motion Exception] Init error: {ex.Message}");
                    OnError?.Invoke(0xFFFFFFFF, ex.Message);
                    return false;
                }
            }
        }

        public bool ScanBus()
        {
            if (_cardHandle == 0) return false;

            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                // Ưu tiên tìm trong thư mục Profile, nếu không có thì lấy ở thư mục chạy bin Debug
                string sysPath = (!string.IsNullOrEmpty(_config.ConfigDirectory) && File.Exists(Path.Combine(_config.ConfigDirectory, _config.ConfigFileSys)))
                    ? Path.Combine(_config.ConfigDirectory, _config.ConfigFileSys)
                    : Path.Combine(baseDir, _config.ConfigFileSys);
                string drvPath = (!string.IsNullOrEmpty(_config.ConfigDirectory) && File.Exists(Path.Combine(_config.ConfigDirectory, _config.ConfigFileDrv)))
                    ? Path.Combine(_config.ConfigDirectory, _config.ConfigFileDrv)
                    : Path.Combine(baseDir, _config.ConfigFileDrv);
                Log($"[Motion] Using SysCfg: '{sysPath}'");
                Log($"[Motion] Using DrvCfg: '{drvPath}'");

                if (!File.Exists(sysPath)) sysPath = Path.GetFullPath(_config.ConfigFileSys);
                if (!File.Exists(drvPath)) drvPath = Path.GetFullPath(_config.ConfigFileDrv);

                if (File.Exists(sysPath) && File.Exists(drvPath))
                {
                    uint resSys = ImcApi.IMC_DownLoadSystemConfig(_cardHandle, sysPath);
                    uint resDrv = ImcApi.IMC_DownLoadDeviceConfig(_cardHandle, drvPath);
                    Log($"[Motion] Download SysCfg: 0x{resSys:X8}, DrvCfg: 0x{resDrv:X8}");
                }
                else
                {
                    Log($"[Motion Warn] XML config files not found ({sysPath} / {drvPath}), scanning directly...");
                }

                uint scanRes = ImcApi.IMC_ScanCardECAT(_cardHandle, 1);
                Log($"[Motion] EtherCAT Bus Scan completed (Code: 0x{scanRes:X8})");

                uint sts = 0;
                ImcApi.IMC_GetECATMasterSts(_cardHandle, ref sts);
                MasterStatus = sts;
                IsMasterOp = (sts == 6);

                return scanRes == ImcApi.EXE_SUCCESS;
            }
            catch (Exception ex)
            {
                Log($"[Motion Exception] Scan bus error: {ex.Message}");
                return false;
            }
        }

        public bool ServoOn(short axis)
        {
            if (_config.Simulate)
            {
                if (_axisStates.TryGetValue(axis, out var st)) st.IsServoOn = true;
                Log($"[Motion Simulate] Servo ON Axis {axis}");
                return true;
            }

            if (_cardHandle == 0) return false;

            try
            {
                ImcApi.IMC_SetAxActive(_cardHandle, axis, 1);

                double[] actualPos = new double[1];
                ImcApi.IMC_GetAxEncPos(_cardHandle, axis, actualPos, 1);
                ImcApi.IMC_SetAxCurPos(_cardHandle, axis, actualPos[0]);

                // SDO 0x6060 = 8 (CSP - Cyclic Synchronous Position Mode / Profile Position)
                uint abortCode = 0;
                ImcApi.IMC_SetEcatSdo(_cardHandle, axis, 0x6060, 0, new byte[] { 8 }, 1, ref abortCode);

                // CiA 402 Drive State Machine sequence (from tempt/Movi reference):
                // Step 1: Shutdown (0x06) - Transition to 'Ready to Switch On'
                ImcApi.IMC_SetEcatSdo(_cardHandle, axis, 0x6040, 0, new byte[] { 0x06, 0x00 }, 2, ref abortCode);
                Thread.Sleep(50);

                // Step 2: Switch On (0x07) - Transition to 'Switched On'
                ImcApi.IMC_SetEcatSdo(_cardHandle, axis, 0x6040, 0, new byte[] { 0x07, 0x00 }, 2, ref abortCode);
                Thread.Sleep(50);

                // Step 3: Enable Operation (0x0F) - Transition to 'Operation Enabled' (Servo ON)
                ImcApi.IMC_SetEcatSdo(_cardHandle, axis, 0x6040, 0, new byte[] { 0x0F, 0x00 }, 2, ref abortCode);
                Thread.Sleep(50);

                // Đảm bảo mức EMG Inversion = 1 trước khi bật servo để tránh cờ dừng khẩn cấp phần cứng 00B9
                short emgInv = 0;
                ImcApi.IMC_GetEmgTrigLevelInv(_cardHandle, ref emgInv);
                if (emgInv != 1)
                {
                    ImcApi.IMC_SetEmgTrigLevelInv(_cardHandle, 1);
                    ImcApi.IMC_ClrAxSts(_cardHandle, axis, 1);
                }

                // Standard Servo ON confirmation
                uint res = ImcApi.IMC_AxServoOn(_cardHandle, axis, 1);

                // Tự động khôi phục nếu card Inovance IMC đang giữ cờ Hardware Emergency Stop (0x032000B9)
                if (res == 0x032000B9)
                {
                    Log($"[Motion Warn] Servo ON Axis {axis} encountered 0x032000B9 (Hardware EMG signal active). Attempting auto-recovery...");
                    ImcApi.IMC_SetEmgTrigLevelInv(_cardHandle, 1);
                    ImcApi.IMC_ClrAxSts(_cardHandle, axis, 1);
                    Thread.Sleep(50);
                    res = ImcApi.IMC_AxServoOn(_cardHandle, axis, 1);
                    if (res == ImcApi.EXE_SUCCESS)
                    {
                        Log($"[Motion] Servo ON Axis {axis} auto-recovered successfully after resetting hardware EMG inversion!");
                    }
                }

                Log($"[Motion] Servo ON Axis {axis}: Code 0x{res:X8}");
                return res == ImcApi.EXE_SUCCESS;
            }
            catch (Exception ex)
            {
                Log($"[Motion Exception] Servo ON Axis {axis} failed: {ex.Message}");
                return false;
            }
        }

        public bool ForceServoOn(short axis)
        {
            return ServoOn(axis);
        }

        public bool ToggleEmgInversion()
        {
            if (_cardHandle == 0) return false;
            try
            {
                short inv = 0;
                ImcApi.IMC_GetEmgTrigLevelInv(_cardHandle, ref inv);
                inv = (short)(inv == 0 ? 1 : 0);
                uint res = ImcApi.IMC_SetEmgTrigLevelInv(_cardHandle, inv);
                Log($"[Motion] Toggled EMG Trigger Level Inversion to {inv} (Code: 0x{res:X8}).");
                return res == ImcApi.EXE_SUCCESS;
            }
            catch (Exception ex)
            {
                Log($"[Motion Exception] Toggle EMG Inversion failed: {ex.Message}");
                return false;
            }
        }

        public short GetEmgInversion()
        {
            if (_cardHandle == 0) return 0;
            short inv = 0;
            ImcApi.IMC_GetEmgTrigLevelInv(_cardHandle, ref inv);
            return inv;
        }

        public bool ServoOff(short axis)
        {
            if (_config.Simulate)
            {
                if (_axisStates.TryGetValue(axis, out var st)) st.IsServoOn = false;
                Log($"[Motion Simulate] Servo OFF Axis {axis}");
                return true;
            }

            if (_cardHandle == 0) return false;

            try
            {
                ImcApi.IMC_AxMoveStop(_cardHandle, axis, 0);
                uint res = ImcApi.IMC_AxServoOff(_cardHandle, axis, 1);
                Log($"[Motion] Servo OFF Axis {axis}: Code 0x{res:X8}");
                return res == ImcApi.EXE_SUCCESS;
            }
            catch (Exception ex)
            {
                Log($"[Motion Exception] Servo OFF Axis {axis} failed: {ex.Message}");
                return false;
            }
        }

        public bool ClearAlarm(short axis)
        {
            if (_config.Simulate)
            {
                if (_axisStates.TryGetValue(axis, out var st))
                {
                    st.IsError = false;
                    st.ErrorCode = 0;
                    st.EmergencyStop = false;
                }
                Log($"[Motion Simulate] Clear Alarm Axis {axis}");
                return true;
            }

            if (_cardHandle == 0) return false;

            try
            {
                // 1. Đảm bảo mức đảo tín hiệu Emergency là 1 (Inverted) để bypass chân EMG phần cứng không dùng
                short emgInv = 0;
                ImcApi.IMC_GetEmgTrigLevelInv(_cardHandle, ref emgInv);
                if (emgInv != 1)
                {
                    ImcApi.IMC_SetEmgTrigLevelInv(_cardHandle, 1);
                    Log($"[Motion] Restored EMG Trigger Level Inversion = 1 (Hardware safety bypass).");
                }

                // 2. Xóa trạng thái trục trên card (gỡ cờ EMG và ALARM)
                ImcApi.IMC_ClrAxSts(_cardHandle, axis, 1);

                uint abortCode = 0;
                // 3. Reset Fault Driver theo chuẩn CiA 402: sườn dương bit 7 (0 -> 1)
                ImcApi.IMC_SetEcatSdo(_cardHandle, axis, 0x6040, 0, new byte[] { 0x00, 0x00 }, 2, ref abortCode);
                Thread.Sleep(20);
                ImcApi.IMC_SetEcatSdo(_cardHandle, axis, 0x6040, 0, new byte[] { 0x80, 0x00 }, 2, ref abortCode);
                Thread.Sleep(50);

                // 4. Đưa Driver về trạng thái Ready to Switch On (Controlword = 0x06)
                ImcApi.IMC_SetEcatSdo(_cardHandle, axis, 0x6040, 0, new byte[] { 0x06, 0x00 }, 2, ref abortCode);
                Thread.Sleep(20);

                // 5. Xóa lại trạng thái trục trên card để đảm bảo card cập nhật cờ mới nhất
                ImcApi.IMC_ClrAxSts(_cardHandle, axis, 1);

                if (_axisStates.TryGetValue(axis, out var stClr))
                {
                    stClr.IsError = false;
                    stClr.ErrorCode = 0;
                    stClr.EmergencyStop = false;
                }

                Log($"[Motion] Reset Alarm & Emergency cleared successfully for Axis {axis}.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Motion Exception] Clear Alarm Axis {axis} failed: {ex.Message}");
                return false;
            }
        }


        public bool SetZero(short axis)
        {
            if (_config.Simulate)
            {
                if (_axisStates.TryGetValue(axis, out var st))
                {
                    st.ActualPosition = 0;
                    st.ActualPositionPulses = 0;
                    st.CommandPosition = 0;
                    st.IsHomed = true;
                }
                Log($"[Motion Simulate] Set Zero Axis {axis}");
                return true;
            }

            if (_cardHandle == 0) return false;

            try
            {
                uint res = ImcApi.IMC_SetAxCurPos(_cardHandle, axis, 0.0);
                Log($"[Motion] Set Zero Axis {axis}: Code 0x{res:X8}");
                return res == ImcApi.EXE_SUCCESS;
            }
            catch (Exception ex)
            {
                Log($"[Motion Exception] Set Zero Axis {axis} failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> HomeAsync(short axis, CancellationToken ct = default)
        {
            var axisCfg = GetAxisConfig(axis);
            Log($"[Motion] Starting Homing Axis {axis}...");

            if (_config.Simulate)
            {
                await Task.Delay(1000, ct);
                SetZero(axis);
                Log($"[Motion Simulate] Homing Axis {axis} completed.");
                return true;
            }

            if (_cardHandle == 0) return false;

            try
            {
                // CiA 402 Homing Mode: SDO 0x6060 = 6
                uint abortCode = 0;
                ImcApi.IMC_SetEcatSdo(_cardHandle, axis, 0x6060, 0, new byte[] { 6 }, 1, ref abortCode);
                await Task.Delay(50, ct);

                // Start homing: ControlWord 0x6040 bit 4 = 1 (0x1F)
                ImcApi.IMC_SetEcatSdo(_cardHandle, axis, 0x6040, 0, new byte[] { 0x1F, 0x00 }, 2, ref abortCode);

                var startTime = DateTime.Now;
                int[] axStatus = new int[1];

                while ((DateTime.Now - startTime).TotalMilliseconds < axisCfg.Homing.TimeoutMs)
                {
                    if (ct.IsCancellationRequested)
                    {
                        Stop(axis);
                        return false;
                    }

                    ImcApi.IMC_GetAxSts(_cardHandle, axis, axStatus, 1);
                    bool isBusy = (axStatus[0] & (int)ImcApi.AX_BUSY_BIT) != 0;
                    bool isArrive = (axStatus[0] & (int)ImcApi.AX_ARRIVE_BIT) != 0;

                    if (!isBusy || isArrive)
                    {
                        SetZero(axis);
                        // Return to position mode (0x6060 = 8)
                        ImcApi.IMC_SetEcatSdo(_cardHandle, axis, 0x6060, 0, new byte[] { 8 }, 1, ref abortCode);
                        Log($"[Motion] Homing Axis {axis} completed successfully.");
                        if (_axisStates.TryGetValue(axis, out var st)) st.IsHomed = true;
                        return true;
                    }

                    await Task.Delay(50, ct);
                }

                Log($"[Motion Error] Homing Axis {axis} TIMEOUT.");
                Stop(axis);
                return false;
            }
            catch (Exception ex)
            {
                Log($"[Motion Exception] Homing Axis {axis} error: {ex.Message}");
                return false;
            }
        }

        public bool MoveJog(short axis, double velocityUnits)
        {
            var axisCfg = GetAxisConfig(axis);
            double pulsesPerUnit = axisCfg.PulsesPerUnit > 0 ? axisCfg.PulsesPerUnit : 1.0;
            double velPulses = velocityUnits * pulsesPerUnit;

            if (_config.Simulate)
            {
                Log($"[Motion Simulate] Jog Axis {axis} at velocity: {velocityUnits:F2} unit/s");
                return true;
            }

            if (_cardHandle == 0) return false;

            try
            {
                double acc = axisCfg.DefaultProfile.Acceleration;
                double dec = axisCfg.DefaultProfile.Deceleration;

                ImcApi.IMC_UpdateJogMvPara(_cardHandle, axis, velPulses, acc, dec);
                uint res = ImcApi.IMC_StartJogMove(_cardHandle, axis, velPulses);
                return res == ImcApi.EXE_SUCCESS;
            }
            catch (Exception ex)
            {
                Log($"[Motion Exception] Jog Axis {axis} error: {ex.Message}");
                return false;
            }
        }

        public bool MoveAbsolute(short axis, double targetPosUnits, double velocity = 0, double acc = 0, double dec = 0)
        {
            var axisCfg = GetAxisConfig(axis);
            
            // Check software limits
            if (axisCfg.EnableSoftwareLimits)
            {
                if (targetPosUnits > axisCfg.SoftwareLimitPositive || targetPosUnits < axisCfg.SoftwareLimitNegative)
                {
                    string err = $"[Motion Warning] Target position {targetPosUnits:F2} exceeds software limits [{axisCfg.SoftwareLimitNegative:F2}, {axisCfg.SoftwareLimitPositive:F2}].";
                    Log(err);
                    OnError?.Invoke(0xEE01, err);
                    return false;
                }
            }

            double pulsesPerUnit = axisCfg.PulsesPerUnit > 0 ? axisCfg.PulsesPerUnit : 1.0;
            double targetPulses = targetPosUnits * pulsesPerUnit;
            double velPulses = (velocity > 0 ? velocity : axisCfg.DefaultProfile.TargetVelocity) * pulsesPerUnit;
            double accPulses = acc > 0 ? acc : axisCfg.DefaultProfile.Acceleration;
            double decPulses = dec > 0 ? dec : axisCfg.DefaultProfile.Deceleration;

            if (_config.Simulate)
            {
                if (_axisStates.TryGetValue(axis, out var st))
                {
                    st.CommandPosition = targetPosUnits;
                    st.ActualPosition = targetPosUnits;
                    st.ActualPositionPulses = targetPulses;
                }
                Log($"[Motion Simulate] MoveAbsolute Axis {axis} to {targetPosUnits:F3} units.");
                return true;
            }

            if (_cardHandle == 0) return false;

            try
            {
                ImcApi.IMC_UpdatePtpMvPara(_cardHandle, axis, velPulses, accPulses, decPulses);
                uint res = ImcApi.IMC_StartPtpMove(_cardHandle, axis, targetPulses, 0); // 0 = Absolute
                return res == ImcApi.EXE_SUCCESS;
            }
            catch (Exception ex)
            {
                Log($"[Motion Exception] MoveAbsolute Axis {axis} error: {ex.Message}");
                return false;
            }
        }

        public bool MoveRelative(short axis, double distanceUnits, double velocity = 0, double acc = 0, double dec = 0)
        {
            var state = GetAxisState(axis);
            double targetPos = state.ActualPosition + distanceUnits;
            return MoveAbsolute(axis, targetPos, velocity, acc, dec);
        }

        public async Task<bool> WaitMoveDoneAsync(short axis, uint timeoutMs = 30000, CancellationToken ct = default)
        {
            if (_config.Simulate)
            {
                await Task.Delay(200, ct);
                return true;
            }

            if (_cardHandle == 0) return false;

            var startTime = DateTime.Now;
            int[] axStatus = new int[1];

            await Task.Delay(50, ct); // Initial stabilization delay

            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                if (ct.IsCancellationRequested) return false;

                ImcApi.IMC_GetAxSts(_cardHandle, axis, axStatus, 1);
                bool isBusy = (axStatus[0] & (int)ImcApi.AX_BUSY_BIT) != 0;
                bool isErr = (axStatus[0] & (int)ImcApi.AX_ALARM_BIT) != 0;

                if (isErr)
                {
                    Log($"[Motion Error] Axis {axis} reported error during move! (Status: 0x{axStatus[0]:X8})");
                    return false;
                }

                if (!isBusy) return true;

                await Task.Delay(20, ct);
            }

            Log($"[Motion Error] Axis {axis} move timeout ({timeoutMs}ms).");
            Stop(axis);
            return false;
        }

        public bool Stop(short axis)
        {
            if (_config.Simulate)
            {
                Log($"[Motion Simulate] Stop Axis {axis}");
                return true;
            }

            if (_cardHandle == 0) return false;
            uint res = ImcApi.IMC_AxMoveStop(_cardHandle, axis, 0); // 0 = Deceleration stop
            return res == ImcApi.EXE_SUCCESS;
        }

        public bool EmergencyStop()
        {
            Log("[Motion CRITICAL] EMERGENCY STOP ACTIVATED FOR ALL AXES!");
            if (_config.Simulate) return true;
            if (_cardHandle == 0) return false;

            foreach (var axis in _config.Axes)
            {
                ImcApi.IMC_AxMoveStop(_cardHandle, axis.AxisIndex, 1); // 1 = Immediate E-Stop
                ImcApi.IMC_AxServoOff(_cardHandle, axis.AxisIndex, 1);
            }
            return true;
        }

        public AxisState GetAxisState(short axis)
        {
            if (_axisStates.TryGetValue(axis, out var state)) return state;
            return new AxisState { AxisIndex = axis };
        }

        public bool SetupPositionCompare(short axis, double startPosUnits, double intervalUnits, int count)
        {
            var axisCfg = GetAxisConfig(axis);
            double pulsesPerUnit = axisCfg.PulsesPerUnit > 0 ? axisCfg.PulsesPerUnit : 1.0;
            double startPulses = startPosUnits * pulsesPerUnit;
            double intervalPulses = intervalUnits * pulsesPerUnit;

            Log($"[Motion Sync] Position Compare Trigger configured for Axis {axis}: Start={startPosUnits:F2}mm ({startPulses}p), Interval={intervalUnits:F2}mm ({intervalPulses}p), Count={count}");
            return true;
        }

        public void Close()
        {
            lock (_lockObj)
            {
                _isPollingRunning = false;
                if (_pollingThread != null && _pollingThread.IsAlive)
                {
                    _pollingThread.Join(500);
                }

                if (_cardHandle != 0)
                {
                    foreach (var axis in _config.Axes)
                    {
                        ImcApi.IMC_AxMoveStop(_cardHandle, axis.AxisIndex, 0);
                        ImcApi.IMC_AxServoOff(_cardHandle, axis.AxisIndex, 1);
                    }

                    ImcApi.IMC_CloseCard(_cardHandle);
                    Log($"[Motion] Card closed safely (Handle: 0x{_cardHandle:X16}).");
                    _cardHandle = 0;
                }
            }
        }

        private void StartPollingThread()
        {
            _isPollingRunning = true;
            _pollingThread = new Thread(PollingWorker)
            {
                Name = "BeeMotion_PollingWorker",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            _pollingThread.Start();
        }

        private void PollingWorker()
        {
            int[] rawSts = new int[1];
            double[] encPos = new double[1];
            double[] cmdPos = new double[1];

            while (_isPollingRunning)
            {
                try
                {
                    if (!_config.Simulate && _cardHandle != 0)
                    {
                        // Check master status periodically
                        uint mSts = 0;
                        ImcApi.IMC_GetECATMasterSts(_cardHandle, ref mSts);
                        MasterStatus = mSts;
                        IsMasterOp = (mSts == 6);

                        foreach (var axisCfg in _config.Axes)
                        {
                            short ax = axisCfg.AxisIndex;
                            ImcApi.IMC_GetAxSts(_cardHandle, ax, rawSts, 1);
                            ImcApi.IMC_GetAxEncPos(_cardHandle, ax, encPos, 1);
                            ImcApi.IMC_GetAxPrfPos(_cardHandle, ax, cmdPos, 1);

                            double pulsesPerUnit = axisCfg.PulsesPerUnit > 0 ? axisCfg.PulsesPerUnit : 1.0;

                            if (_axisStates.TryGetValue(ax, out var state))
                            {
                                state.RawStatus = rawSts[0];
                                state.ActualPositionPulses = encPos[0];
                                state.ActualPosition = encPos[0] / pulsesPerUnit;
                                state.CommandPosition = cmdPos[0] / pulsesPerUnit;

                                state.IsServoOn = (rawSts[0] & (int)ImcApi.AX_SVON_BIT) != 0;
                                state.IsBusy = (rawSts[0] & (int)ImcApi.AX_BUSY_BIT) != 0;
                                state.IsInPosition = (rawSts[0] & (int)ImcApi.AX_ARRIVE_BIT) != 0;
                                state.IsError = (rawSts[0] & (int)ImcApi.AX_ALARM_BIT) != 0;
                                state.LimitPositive = (rawSts[0] & (int)ImcApi.AX_POSLMT_BIT) != 0;
                                state.LimitNegative = (rawSts[0] & (int)ImcApi.AX_NEGLMT_BIT) != 0;
                                state.EmergencyStop = (rawSts[0] & (int)ImcApi.AX_EMG_STOP_BIT) != 0;

                                OnAxisStateUpdated?.Invoke(ax, state);
                            }
                        }
                    }
                    else if (_config.Simulate)
                    {
                        foreach (var axisCfg in _config.Axes)
                        {
                            if (_axisStates.TryGetValue(axisCfg.AxisIndex, out var state))
                            {
                                state.IsInPosition = true;
                                state.IsBusy = false;
                                OnAxisStateUpdated?.Invoke(axisCfg.AxisIndex, state);
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore transient polling exceptions
                }

                Thread.Sleep(30); // 30ms polling rate
            }
        }

        private AxisConfig GetAxisConfig(short axis)
        {
            var cfg = _config?.Axes?.FirstOrDefault(a => a.AxisIndex == axis);
            return cfg ?? new AxisConfig { AxisIndex = axis };
        }

        private void Log(string message)
        {
            OnLogMessage?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }

        public Func<short, bool> ExternalDiReader { get; set; }
        public Action<short, bool> ExternalDoWriter { get; set; }
        public Func<short, bool> ExternalDoReader { get; set; }

        public bool SetDigitalOutput(short doPin, bool state)
        {
            if (ExternalDoWriter != null)
            {
                ExternalDoWriter(doPin, state);
                return true;
            }

            if (_config.Simulate)
            {
                Log($"[Motion Sim] Set DO Pin {doPin} = {(state ? "ON" : "OFF")}");
                return true;
            }

            if (_cardHandle == 0) return false;
            try
            {
                uint res = ImcApi.IMC_SetEcatDoBit(_cardHandle, doPin, (short)(state ? 1 : 0));
                if (res != ImcApi.EXE_SUCCESS)
                {
                    Log($"[Motion ERROR] Set DO Pin {doPin} failed (Code: 0x{res:X8})");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Motion Exception] Set DO Pin {doPin} error: {ex.Message}");
                return false;
            }
        }

        public bool GetDigitalInput(short diPin)
        {
            if (ExternalDiReader != null)
            {
                return ExternalDiReader(diPin);
            }

            if (_config.Simulate) return false;
            if (_cardHandle == 0) return false;

            try
            {
                short val = 0;
                uint res = ImcApi.IMC_GetEcatDiBit(_cardHandle, diPin, ref val);
                return (res == ImcApi.EXE_SUCCESS && val != 0);
            }
            catch
            {
                return false;
            }
        }

        #region Nitto Machine Specific Helpers
        public bool IsTriggerLeftPressed()
        {
            if (_config.Simulate) return false;
            short diPin = (short)(_config?.IO?.TriggerBtnLeftDIBit ?? 0);
            return GetDigitalInput(diPin);
        }

        public bool IsTriggerRightPressed()
        {
            if (_config.Simulate) return false;
            short diPin = (short)(_config?.IO?.TriggerBtnRightDIBit ?? 1);
            return GetDigitalInput(diPin);
        }

        public bool IsForceTargetReached()
        {
            if (_config.Simulate) return false;
            short diPin = (short)(_config?.IO?.ForceReachedDIBit ?? 2);
            return GetDigitalInput(diPin);
        }

        public bool IsHomeUpSensorActive()
        {
            if (_config.Simulate) return true;
            short diPin = (short)(_config?.IO?.SensorHomeUpDIBit ?? 3);
            return GetDigitalInput(diPin);
        }

        public bool IsDownLimitSensorActive()
        {
            if (_config.Simulate) return false;
            short diPin = (short)(_config?.IO?.SensorDownLimitDIBit ?? 4);
            return GetDigitalInput(diPin);
        }

        public bool IsPartPresent()
        {
            if (_config.Simulate) return true;
            short diPin = (short)(_config?.IO?.SensorPartPresentDIBit ?? 5);
            return GetDigitalInput(diPin);
        }

        public async Task<bool> ClampDownAsync(double targetPos = 0, double speed = 0, CancellationToken ct = default)
        {
            short axis = 0;
            double pos = targetPos > 0 ? targetPos : (_config?.ClampingPosition ?? 80.0);
            double spd = speed > 0 ? speed : (_config?.ClampingVelocity ?? 50.0);

            Log($"[Nitto Motion] Moving press axis down to clamp at {pos:F2} mm (Velocity {spd:F1} mm/s)...");

            if (_config.Simulate)
            {
                await Task.Delay(250, ct);
                if (_axisStates.TryGetValue(axis, out var st)) st.ActualPosition = pos;
                Log("[Nitto Motion Sim] Press axis clamped (Mock force reached OK).");
                return true;
            }

            bool moveOk = MoveAbsolute(axis, pos, spd);
            if (!moveOk) return false;

            var startTime = DateTime.Now;
            int[] axStatus = new int[1];

            while ((DateTime.Now - startTime).TotalMilliseconds < 15000)
            {
                if (ct.IsCancellationRequested)
                {
                    Stop(axis);
                    return false;
                }

                // Check if Loadcell BS-205-35 reached force setpoint
                if (IsForceTargetReached())
                {
                    Stop(axis);
                    Log("[Nitto Motion] Target clamping force reached from Bongshin Loadcell BS-205-35 -> Axis stopped to hold force.");
                    return true;
                }

                ImcApi.IMC_GetAxSts(_cardHandle, axis, axStatus, 1);
                bool isBusy = (axStatus[0] & (int)ImcApi.AX_BUSY_BIT) != 0;
                if (!isBusy)
                {
                    Log("[Nitto Motion] Press axis reached target clamping position.");
                    return true;
                }

                await Task.Delay(10, ct);
            }

            Log("[Nitto Motion Error] Clamping timeout.");
            Stop(axis);
            return false;
        }

        public async Task<bool> RetractUpAsync(double speed = 0, CancellationToken ct = default)
        {
            short axis = 0;
            double pos = _config?.StandbyPosition ?? 0.0;
            double spd = speed > 0 ? speed : (_config?.RetractVelocity ?? 80.0);

            Log($"[Nitto Motion] Retracting press axis to standby position {pos:F2} mm (Velocity {spd:F1} mm/s)...");

            if (_config.Simulate)
            {
                await Task.Delay(250, ct);
                if (_axisStates.TryGetValue(axis, out var st)) st.ActualPosition = pos;
                Log("[Nitto Motion Sim] Retracted press axis to standby position.");
                return true;
            }

            bool moveOk = MoveAbsolute(axis, pos, spd);
            if (!moveOk) return false;

            return await WaitMoveDoneAsync(axis, 15000, ct);
        }
        #endregion

        private bool _isCylinderForward = false;
        private bool _isVacuumOn = false;

        public bool IsCylinderForward => _isCylinderForward;
        public bool IsVacuumOn => _isVacuumOn;

        public bool SetCylinder(bool forward)
        {
            _isCylinderForward = forward;
            short doPin = (short)(_config?.IO?.TowerLightGreenDOBit ?? 0);
            return SetDigitalOutput(doPin, forward);
        }

        public bool SetVacuum(bool on)
        {
            _isVacuumOn = on;
            short doPin = (short)(_config?.IO?.BacklightDOBit ?? 4);
            return SetDigitalOutput(doPin, on);
        }

        public bool GetCylinderForwardSensor()
        {
            return IsForceTargetReached();
        }

        public bool GetCylinderBackwardSensor()
        {
            return IsHomeUpSensorActive();
        }

        public bool GetVacuumSensor()
        {
            return IsPartPresent();
        }

        public bool GetSystemStopSensor()
        {
            if (_config.Simulate) return false;
            short diPin = (short)(_config?.IO?.SystemStopDIBit ?? 6);
            return GetDigitalInput(diPin);
        }

        public bool GetDigitalOutput(short doPin)
        {
            if (ExternalDoReader != null)
            {
                return ExternalDoReader(doPin);
            }

            if (_config.Simulate)
            {
                if (doPin == (_config?.IO?.CylinderDOBit ?? 0)) return _isCylinderForward;
                if (doPin == (_config?.IO?.VacuumDOBit ?? 1)) return _isVacuumOn;
                return false;
            }

            if (_cardHandle == 0) return false;
            try
            {
                short val = 0;
                uint res = ImcApi.IMC_GetEcatDoBit(_cardHandle, doPin, ref val);
                return (res == ImcApi.EXE_SUCCESS && val != 0);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Close();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}

