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
        public MotionConfig Config
        {
            get => _config;
            set => _config = value ?? new MotionConfig();
        }

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
                Log($"[Motion Simulate] Set Zero Axis {axis} (Virtual Home established)");
                return true;
            }

            if (_cardHandle == 0) return false;

            try
            {
                uint res = ImcApi.IMC_SetAxCurPos(_cardHandle, axis, 0.0);
                if (res == ImcApi.EXE_SUCCESS && _axisStates.TryGetValue(axis, out var state))
                {
                    state.ActualPosition = 0;
                    state.ActualPositionPulses = 0;
                    state.CommandPosition = 0;
                    state.IsHomed = true;
                }
                Log($"[Motion] Set Zero Axis {axis}: Code 0x{res:X8} (Virtual Home established)");
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
            var homingCfg = axisCfg.Homing ?? new HomingConfig();
            Log($"[Motion] Starting native Homing Axis {axis}...");

            if (_axisStates.TryGetValue(axis, out var initialState))
            {
                // Tọa độ đọc lúc khởi động chỉ là phản hồi encoder; chưa được dùng như gốc máy.
                initialState.IsHomed = false;
            }

            if (_config.Simulate)
            {
                await Task.Delay(1000, ct);
                if (_axisStates.TryGetValue(axis, out var simSt))
                {
                    simSt.ActualPosition = homingCfg.OffsetPulses / (axisCfg.PulsesPerUnit > 0 ? axisCfg.PulsesPerUnit : 1.0);
                    simSt.ActualPositionPulses = homingCfg.OffsetPulses;
                    simSt.CommandPosition = simSt.ActualPosition;
                    simSt.IsHomed = true;
                }
                Log($"[Motion Simulate] Homing Axis {axis} completed with method 28.");
                return true;
            }

            if (_cardHandle == 0) return false;

            bool homingModeActive = false;
            try
            {
                int[] rawStatus = new int[1];
                uint statusResult = ImcApi.IMC_GetAxSts(_cardHandle, axis, rawStatus, 1);
                if (statusResult != ImcApi.EXE_SUCCESS)
                {
                    Log($"[Motion Error] Cannot read Axis {axis} status before Homing: Code 0x{statusResult:X8}.");
                    return false;
                }

                if ((rawStatus[0] & (int)ImcApi.AX_SVON_BIT) == 0)
                {
                    Log($"[Motion Error] Cannot Home Axis {axis}: Servo is not ON in the card status.");
                    return false;
                }

                if ((rawStatus[0] & (int)(ImcApi.AX_ALARM_BIT | ImcApi.AX_EMG_STOP_BIT)) != 0)
                {
                    Log($"[Motion Error] Cannot Home Axis {axis}: Alarm or Emergency Stop is active.");
                    return false;
                }

                if ((rawStatus[0] & (int)ImcApi.AX_BUSY_BIT) != 0)
                {
                    Log($"[Motion Error] Cannot Home Axis {axis}: Axis is already moving.");
                    return false;
                }

                const short homeMethod = 28;
                if (homingCfg.HomeMethod != homeMethod)
                {
                    Log($"[Motion] Axis {axis} overrides configured Homing Method {homingCfg.HomeMethod} with production Method 28 (negative Home switch, no Z-index).");
                }

                var parameters = new ImcApi.THomingPara
                {
                    homeMethod = homeMethod,
                    offset = homingCfg.OffsetPulses,
                    highVel = ToPositiveUInt32(homingCfg.HighVelocity, 10000),
                    lowVel = ToPositiveUInt32(homingCfg.LowVelocity, 1000),
                    acc = ToPositiveUInt32(homingCfg.Acceleration, 100000),
                    overtime = homingCfg.TimeoutMs > 0 ? homingCfg.TimeoutMs : 30000,
                    posSrc = 0
                };

                Log($"[Motion] Axis {axis} Homing parameters: Method={parameters.homeMethod}, Direction=Negative, HomeInput=Drive, HighVel={parameters.highVel} pulse/s, LowVel={parameters.lowVel} pulse/s, Acc={parameters.acc} pulse/s^2, Offset={parameters.offset} pulse, Timeout={parameters.overtime} ms.");

                uint startResult = ImcApi.IMC_StartHoming(_cardHandle, axis, ref parameters);
                if (startResult != ImcApi.EXE_SUCCESS)
                {
                    Log($"[Motion Error] Start Homing Axis {axis} failed: Code 0x{startResult:X8}.");
                    return false;
                }

                homingModeActive = true;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                while (stopwatch.ElapsedMilliseconds < parameters.overtime)
                {
                    ct.ThrowIfCancellationRequested();

                    short homingStatus = ImcApi.HOME_IN_PROGRESS;
                    uint homingStatusResult = ImcApi.IMC_GetHomingStatus(_cardHandle, axis, ref homingStatus);
                    if (homingStatusResult != ImcApi.EXE_SUCCESS)
                    {
                        Log($"[Motion Error] Read Homing status Axis {axis} failed: Code 0x{homingStatusResult:X8}.");
                        return false;
                    }

                    if (homingStatus == ImcApi.HOME_SUCESS)
                    {
                        uint finishResult = ImcApi.IMC_FinishHoming(_cardHandle, axis);
                        if (finishResult != ImcApi.EXE_SUCCESS)
                        {
                            Log($"[Motion Error] Finish Homing Axis {axis} failed: Code 0x{finishResult:X8}.");
                            return false;
                        }
                        homingModeActive = false;

                        if (_axisStates.TryGetValue(axis, out var finalState))
                        {
                            finalState.IsHomed = true;
                        }

                        Log($"[Motion] Homing Axis {axis} completed successfully with native status {homingStatus}.");
                        return true;
                    }

                    if (homingStatus != ImcApi.HOME_IN_PROGRESS &&
                        !(homingStatus == ImcApi.HOME_INTERRUPTED_OR_NOT_START && stopwatch.ElapsedMilliseconds < 300))
                    {
                        Log($"[Motion Error] Homing Axis {axis} failed with native status {homingStatus}.");
                        return false;
                    }

                    statusResult = ImcApi.IMC_GetAxSts(_cardHandle, axis, rawStatus, 1);
                    if (statusResult != ImcApi.EXE_SUCCESS ||
                        (rawStatus[0] & (int)(ImcApi.AX_ALARM_BIT | ImcApi.AX_EMG_STOP_BIT)) != 0)
                    {
                        Log($"[Motion Error] Homing Axis {axis} stopped because the axis entered an alarm or emergency state.");
                        return false;
                    }

                    await Task.Delay(20, ct);
                }

                Log($"[Motion Error] Homing Axis {axis} timed out after {parameters.overtime} ms.");
                return false;
            }
            catch (OperationCanceledException)
            {
                Log($"[Motion] Homing Axis {axis} was cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                Log($"[Motion Exception] Homing Axis {axis} error: {ex.Message}");
                return false;
            }
            finally
            {
                if (homingModeActive)
                {
                    uint stopResult = ImcApi.IMC_StopHoming(_cardHandle, axis, 0);
                    uint finishResult = ImcApi.IMC_FinishHoming(_cardHandle, axis);
                    Log($"[Motion] Homing Axis {axis} cleanup: Stop=0x{stopResult:X8}, Finish=0x{finishResult:X8}.");
                }
            }
        }

        private static uint ToPositiveUInt32(double value, uint fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            {
                return fallback;
            }

            if (value >= uint.MaxValue)
            {
                return uint.MaxValue;
            }

            return (uint)Math.Round(value);
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
                if (res != ImcApi.EXE_SUCCESS)
                {
                    Log($"[Motion Warn] StartJogMove Axis {axis} at vel {velPulses:F1} returned code 0x{res:X8}");
                }
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

            AxisState axisState = GetAxisState(axis);
            if (axisState == null || !axisState.IsHomed)
            {
                Log($"[Motion Safety] Absolute move blocked on Axis {axis}: Hardware homing has not completed.");
                return false;
            }
            
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
                if (res != ImcApi.EXE_SUCCESS)
                {
                    Log($"[Motion Warn] StartPtpMove Axis {axis} to {targetPosUnits:F3} returned code 0x{res:X8}");
                }
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
                                bool isNearHomeOrigin = Math.Abs(state.ActualPosition) <= 1.0;
                                state.HomeSensor = ((rawSts[0] & (int)ImcApi.AX_HM_BIT) != 0 && isNearHomeOrigin);

                                // Đọc ngõ vào số trực tiếp từ Driver qua EtherCAT (CiA 402 Object 0x60FD):
                                // Bit 0: Negative limit switch (NOT), Bit 1: Positive limit switch (POT), Bit 2: Home switch (ORG)
                                int ecatDi = 0;
                                uint diRes = ImcApi.IMC_GetAxEcatDigitalInput(_cardHandle, ax, ref ecatDi);
                                if (diRes == ImcApi.EXE_SUCCESS)
                                {
                                    if ((ecatDi & 0x01) != 0) state.LimitNegative = true;
                                    if ((ecatDi & 0x02) != 0) state.LimitPositive = true;
                                    bool ecatOrg = (ecatDi & 0x04) != 0 && isNearHomeOrigin;
                                    state.HomeSensor = state.HomeSensor || ecatOrg || IsHomeUpSensorActive();
                                }
                                else
                                {
                                    // Fallback nếu drive không hỗ trợ 0x60FD hoặc dùng cảm biến gắn ngoài
                                    state.HomeSensor = state.HomeSensor || IsHomeUpSensorActive();
                                }

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
                                state.HomeSensor = Math.Abs(state.ActualPosition) < 0.5;
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
            if (_config.Simulate) return false;
            // Chân DI 11 trong IOConfig máy Nitto là LC_High (hiện chưa dùng làm cảm biến cữ trên)
            // Chỉ đọc nếu chân được cấu hình hợp lệ và khác chân LC_High chưa dùng
            if (_config?.IO?.SensorHomeUpDIBit == null || _config.IO.SensorHomeUpDIBit < 0 || _config.IO.SensorHomeUpDIBit == 11)
            {
                return false;
            }
            short diPin = (short)_config.IO.SensorHomeUpDIBit;
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
            var clampPt = _config?.TeachingPoints?.FirstOrDefault(p => p.TriggerVision || p.StepType == "CheckVision" || p.Id == 2);
            double pos = targetPos > 0 ? targetPos : (clampPt != null && clampPt.Position > 0 ? clampPt.Position : (_config?.ClampingPosition ?? 80.0));
            double spd = speed > 0 ? speed : (clampPt != null && clampPt.Speed > 0 ? clampPt.Speed : (_config?.ClampingVelocity ?? 50.0));
            double jogSpeed = _config?.ClampJogVelocity > 0 ? _config.ClampJogVelocity : 10.0;

            if (GetSystemStopSensor())
            {
                Log("[Nitto Motion Error] Clamp command rejected because the system stop sensor is active.");
                EmergencyStop();
                return false;
            }

            Log($"[Nitto Motion] Resetting load cell before clamp (DO {_config.IO.LoadCellResetDOBit}, pulse 200 ms)...");

            if (_config.Simulate)
            {
                await Task.Delay(200, ct);
                await Task.Delay(250, ct);
                if (_axisStates.TryGetValue(axis, out var st)) st.ActualPosition = pos;
                Log($"[Nitto Motion Sim] Reached Clamp Down position and jogged positive at {jogSpeed:F1} mm/s until Load Cell OK.");
                return true;
            }

            if (!SetDigitalOutput((short)_config.IO.LoadCellResetDOBit, true))
            {
                Log("[Nitto Motion Error] Failed to turn ON LC_Reset output.");
                return false;
            }

            try
            {
                await Task.Delay(200, ct);
            }
            finally
            {
                SetDigitalOutput((short)_config.IO.LoadCellResetDOBit, false);
            }

            Log($"[Nitto Motion] Moving press axis to Clamp Down position {pos:F2} mm (Velocity {spd:F1} mm/s)...");
            bool moveOk = MoveAbsolute(axis, pos, spd);
            if (!moveOk) return false;

            if (!await WaitMoveDoneAsync(axis, 15000, ct))
            {
                Log("[Nitto Motion Error] Failed to reach Clamp Down position.");
                return false;
            }

            if (IsForceTargetReached())
            {
                Log("[Nitto Motion] Load Cell OK was already active at Clamp Down position.");
                return true;
            }

            Log($"[Nitto Motion] Jogging Axis {axis} in positive direction at {jogSpeed:F1} mm/s until Load Cell OK (DI {_config.IO.ForceReachedDIBit})...");
            if (!MoveJog(axis, jogSpeed))
            {
                Log("[Nitto Motion Error] Failed to start clamp jog.");
                return false;
            }

            var startTime = DateTime.Now;
            try
            {
                while ((DateTime.Now - startTime).TotalMilliseconds < 15000)
                {
                    ct.ThrowIfCancellationRequested();

                    if (GetSystemStopSensor())
                    {
                        Log("[Nitto Motion Error] Clamp jog stopped because the system stop sensor is active.");
                        EmergencyStop();
                        return false;
                    }

                    if (IsForceTargetReached())
                    {
                        Log($"[Nitto Motion] Load Cell OK detected on DI{_config.IO.ForceReachedDIBit} -> Clamp jog stopped.");
                        return true;
                    }

                    var axisState = GetAxisState(axis);
                    var axisConfig = GetAxisConfig(axis);
                    if (axisState == null || axisState.IsError || axisState.EmergencyStop)
                    {
                        Log("[Nitto Motion Error] Clamp jog stopped because the axis entered an error or emergency state.");
                        return false;
                    }

                    if (axisConfig.EnableSoftwareLimits && axisState.ActualPosition >= axisConfig.SoftwareLimitPositive)
                    {
                        Log($"[Nitto Motion Error] Clamp jog reached positive software limit {axisConfig.SoftwareLimitPositive:F2} mm before Load Cell OK.");
                        return false;
                    }

                    await Task.Delay(10, ct);
                }

                Log($"[Nitto Motion Error] Clamp jog timeout while waiting for Load Cell OK on DI{_config.IO.ForceReachedDIBit}.");
                return false;
            }
            finally
            {
                Stop(axis);
            }
        }

        public async Task<bool> RetractUpAsync(double speed = 0, CancellationToken ct = default)
        {
            short axis = 0;
            var standbyPt = _config?.TeachingPoints?.FirstOrDefault(p => p.StepType == "Standby" || p.Id == 1);
            double pos = standbyPt != null ? standbyPt.Position : (_config?.StandbyPosition ?? 0.0);
            double spd = speed > 0 ? speed : (standbyPt != null && standbyPt.Speed > 0 ? standbyPt.Speed : (_config?.RetractVelocity ?? 80.0));

            if (GetSystemStopSensor())
            {
                Log("[Nitto Motion Error] Retract command rejected because the system stop sensor is active.");
                EmergencyStop();
                return false;
            }

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
            short diPin = (short)(_config?.IO?.SystemStopDIBit ?? -1);
            if (diPin < 0) return false;
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

