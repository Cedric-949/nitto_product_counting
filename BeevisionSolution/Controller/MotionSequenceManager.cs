using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeeMotionModule;
using BeeMotionModule.Models;
using BeevisionSolution.Jobs;
using log4net;

namespace BeevisionSolution.Controller
{
    public enum SequenceState
    {
        Idle,                   // Vị trí chờ mở kẹp (0mm)
        CheckingReady,          // Kiểm tra servo & an toàn
        WaitingTrigger,         // Chờ nhấn 2 nút trigger IDEC
        ClampingDown,           // Hạ cơ cấu tỳ kẹp phẳng tệp sản phẩm (Leadshine EL7)
        TriggeringVision,       // Bật Backlight, duy trì lực tỳ ổn định
        ProcessingVision,       // VisionPro đếm 100pcs và kiểm tra ngược mặt
        UnclampingUp,           // Nâng cơ cấu tỳ về vị trí mở kẹp
        FinishingCycle,         // Cập nhật kết quả, bật đèn tháp OK/NG
        Error,                  // Báo lỗi
        Stopped,                // Dừng
        // Backward compatibility
        TrayIn = ClampingDown,
        MovingToCapture = ClampingDown,
        CompensatingAndAction = ProcessingVision,
        MovingToEnd = UnclampingUp,
        TrayOut = UnclampingUp
    }

    /// <summary>
    /// Coordinates the automated production cycle for Nitto Product Counting & Reverse Inspection:
    /// Standby -> Two-Hand Trigger -> Clamp Down (Force Control) -> Vision Counting -> Retract Up -> Report.
    /// </summary>
    public class MotionSequenceManager
    {
        private static readonly Lazy<MotionSequenceManager> _instance = new Lazy<MotionSequenceManager>(() => new MotionSequenceManager());
        public static MotionSequenceManager Instance => _instance.Value;

        public IMotionController Motion { get; private set; }
        public SequenceState CurrentState { get; private set; } = SequenceState.Idle;
        public bool IsRunning { get; private set; } = false;
        public bool IsContinuousMode { get; set; } = false;
        public int TotalCycleCount { get; private set; } = 0;
        public double LastCycleTimeMs { get; private set; } = 0;

        // Test Program & Mock Simulation Properties
        public bool IsTestProgramMode { get; set; } = false;
        public string MockVisionResult { get; set; } = "OK"; // "OK", "NG1", "NG2", "NG3"
        public int WatchdogTimeoutMs { get; set; } = 30000;
        public short BrakeDOPin { get; set; } = 1;
        public event Action<SequenceState> OnStateChanged;
        public event Action<string> OnLog;
        public event Action<int, double, bool> OnCycleCompleted; // cycleCount, cycleTimeMs, isOk

        private CancellationTokenSource _cts;
        private readonly Stopwatch _cycleStopwatch = new Stopwatch();

        private MotionSequenceManager()
        {
            Motion = new InovanceEcatController();
            Motion.OnLogMessage += (msg) => OnLog?.Invoke(msg);
            AttachPCIeIO();
        }

        /// <summary>
        /// Links PCIe IO Card (PCIE-E2I12O16) to Motion Controller so machine cycle and UI use PCIe IO channels.
        /// </summary>
        public void AttachPCIeIO()
        {
            try
            {
                var pcieIo = IoJobCtrl.GetIOcardCtrl();
                if (pcieIo != null)
                {
                    if (!pcieIo.IsInit)
                    {
                        pcieIo.InitGPIO();
                        if (pcieIo.IsInit)
                        {
                            var ioThread = new Thread(pcieIo.DoSync) { IsBackground = true };
                            ioThread.Start();
                        }
                    }

                    if (pcieIo.IsInit && Motion != null)
                    {
                        Motion.ExternalDiReader = pin => pcieIo.GetChannelInput((int)pin);
                        Motion.ExternalDoWriter = (pin, val) => pcieIo.SetChannelOutput((int)pin, val);
                        Motion.ExternalDoReader = pin => pcieIo.GetChannelOutput((int)pin);
                        Log("[Sequence] Linked PCIe IO Card (PCIE-E2I12O16) to Motion & Sequence subsystem.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[Sequence Exception] AttachPCIeIO error: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads Digital Input state from PCIe IO Card (via IoJobCtrl) or fallback to Motion Card.
        /// </summary>
        public bool GetDigitalInput(int pinNo)
        {
            // Ưu tiên đọc từ Card PCIe IO rời (PCIE-E2I12O16)
            var pcieIo = IoJobCtrl.GetIOcardCtrl();
            if (pcieIo != null && pcieIo.IsInit)
            {
                return pcieIo.GetChannelInput(pinNo);
            }

            // Nếu không có card PCIe thì đọc từ Card Motion Inovance
            if (Motion != null)
            {
                return Motion.GetDigitalInput((short)pinNo);
            }

            return false;
        }

        /// <summary>
        /// Sets Digital Output state on PCIe IO Card or fallback to Motion Card.
        /// </summary>
        public bool SetDigitalOutput(int pinNo, bool state)
        {
            var pcieIo = IoJobCtrl.GetIOcardCtrl();
            if (pcieIo != null && pcieIo.IsInit)
            {
                return pcieIo.SetChannelOutput(pinNo, state);
            }

            if (Motion != null)
            {
                return Motion.SetDigitalOutput((short)pinNo, state);
            }

            return false;
        }

        public bool Initialize(MotionConfig config)
        {
            Log("[Sequence] Initializing Nitto Motion Control subsystem...");
            AttachPCIeIO();
            bool ok = Motion.Init(config);
            if (ok)
            {
                SetState(SequenceState.Idle);
                Log("[Sequence] Nitto Motion Control system is ready.");
            }
            else
            {
                SetState(SequenceState.Error);
                Log("[Sequence Error] Motion initialization failed.");
            }
            return ok;
        }
        /// <summary>
        /// Standard Sequence: Release Brake (PCIe DO) -> Clear Emergency/Alarm -> Servo ON
        /// </summary>
        public async Task<bool> EnableServoSequenceAsync(short axis = 0, CancellationToken ct = default)
        {
            if (Motion == null) return false;

            try
            {
                Log($"[Servo Sequence] Step 1: Releasing motor brake via PCIe DO {BrakeDOPin}...");
                var pcieIo = IoJobCtrl.GetIOcardCtrl();
                if (pcieIo != null && pcieIo.IsInit)
                {
                    pcieIo.SetPinOutput(BrakeDOPin, true); // ON chân nhả phanh
                }
                else
                {
                    // Fallback sang motion nếu dùng onboard
                    Motion.SetDigitalOutput(BrakeDOPin, true);
                }
                await Task.Delay(150, ct); // Chờ rơle mở phanh vật lý

                Log($"[Servo Sequence] Step 2: Clearing Emergency & Resetting Driver Alarm for Axis {axis}...");
                Motion.ClearAlarm(axis);
                await Task.Delay(100, ct); // Chờ driver xóa lỗi

                Log($"[Servo Sequence] Step 3: Turning Servo ON for Axis {axis}...");
                bool svOk = Motion.ServoOn(axis);
                if (!svOk)
                {
                    Log($"[Servo Sequence Error] Axis {axis}: Servo ON command failed!");
                    return false;
                }

                Log($"[Servo Sequence] Step 4: Waiting for Axis {axis} Servo ON status...");
                if (await WaitForServoOnAsync(axis, 2000, ct))
                {
                    Log($"[Servo Sequence] >>> Axis {axis}: Servo ON status confirmed!");
                    return true;
                }

                Log($"[Servo Sequence Error] Axis {axis}: Servo ON status was not confirmed within 2000 ms!");
                return false;
            }
            catch (Exception ex)
            {
                Log($"[Servo Sequence Exception] Axis {axis}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> WaitForServoOnAsync(short axis, int timeoutMs, CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();

                AxisState state = Motion.GetAxisState(axis);
                if (state != null && state.IsServoOn)
                {
                    return true;
                }

                if (state != null && (state.IsError || state.EmergencyStop))
                {
                    return false;
                }

                await Task.Delay(20, ct);
            }

            return false;
        }

        public async Task<bool> StartCycleAsync(bool continuous = false, Func<int, Task<bool>> onVisionJobTrigger = null)
        {
            if (IsRunning)
            {
                Log("[Sequence Warn] Cycle is already running. Ignoring new trigger.");
                return false;
            }

            IsRunning = true;
            IsContinuousMode = continuous;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Log($"[Sequence] Starting Nitto production cycle (Mode: {(continuous ? "Continuous / Auto" : "Single Cycle")}, TestMode: {IsTestProgramMode})...");

            try
            {
                while (IsRunning && !token.IsCancellationRequested)
                {
                    _cycleStopwatch.Restart();
                    bool cycleSuccess = await ExecuteSingleCycleAsync(onVisionJobTrigger, token);
                    _cycleStopwatch.Stop();

                    LastCycleTimeMs = _cycleStopwatch.Elapsed.TotalMilliseconds;
                    if (cycleSuccess) TotalCycleCount++;

                    OnCycleCompleted?.Invoke(TotalCycleCount, LastCycleTimeMs, cycleSuccess);

                    if (!IsContinuousMode || token.IsCancellationRequested || !cycleSuccess)
                    {
                        break;
                    }

                    int dwell = Motion.Config?.DwellTimeMs > 0 ? Motion.Config.DwellTimeMs : 300;
                    await Task.Delay(dwell, token);
                }
            }
            catch (OperationCanceledException)
            {
                Log("[Sequence] Cycle stopped by user request.");
            }
            catch (Exception ex)
            {
                Log($"[Sequence Exception] Cycle error: {ex.Message}");
                SetState(SequenceState.Error);
            }
            finally
            {
                IsRunning = false;
                SetState(SequenceState.Idle);
            }

            return true;
        }

        public Task<bool> StartCycleAsync(bool continuous, Func<Task<bool>> onVisionProcessTrigger)
        {
            return StartCycleAsync(continuous, onVisionProcessTrigger != null ? new Func<int, Task<bool>>(jobId => onVisionProcessTrigger()) : null);
        }
        // Default DI pin definitions for two-hand start buttons and Loadcell sensor
        public short StartBtn1DIPin { get; set; } = 4; // DI 4: Left Start button
        public short StartBtn2DIPin { get; set; } = 5; // DI 5: Right Start button
        public short LoadcellDIPin { get; set; } = 6;  // DI 6: Loadcell sensor

        public async Task<bool> SimulateTriggerAsync()
        {
            Log("[Sequence] Simulating 2-hand trigger -> Starting inspection cycle...");
            return await StartCycleAsync(false);
        }

        private async Task<bool> ExecuteSingleCycleAsync(Func<int, Task<bool>> onVisionJobTrigger, CancellationToken ct)
        {
            short axis = 0;
            var cfg = Motion.Config;

           
            // STEP 1: Safety & Servo Status Check
            SetState(SequenceState.CheckingReady);
            if (!Motion.IsMasterOp && !cfg.Simulate)
            {
                Log("[Sequence Error] EtherCAT Master is not in OP state (State 6). Please check network cable and driver.");
                SetState(SequenceState.Error);
                return false;
            }

            if (!Motion.GetAxisState(axis).IsServoOn)
            {
                bool svOk = await EnableServoSequenceAsync(axis, ct);
                if (!svOk)
                {
                    Log("[Cycle Error] Failed to enable Servo. Cycle aborted.");
                    SetState(SequenceState.Error);
                    return false;
                }
                await Task.Delay(150, ct);
            }

            // STEP 2: Waiting for Two-Hand Trigger buttons (if in Auto / Continuous Mode and not simulation)
            if (IsContinuousMode && !IsTestProgramMode && !cfg.Simulate)
            {
                SetState(SequenceState.WaitingTrigger);
                Log($"[Sequence] Waiting for operator to press both safety trigger buttons simultaneously (DI {StartBtn1DIPin} & DI {StartBtn2DIPin})...");

                bool triggered = false;
                while (!ct.IsCancellationRequested && IsRunning)
                {
                    bool left = Motion.IsTriggerLeftPressed() || Motion.GetDigitalInput(StartBtn1DIPin);
                    bool right = Motion.IsTriggerRightPressed() || Motion.GetDigitalInput(StartBtn2DIPin);

                    if (left && right)
                    {
                        await Task.Delay(30, ct); // Debounce 30ms
                        bool leftDebounce = Motion.IsTriggerLeftPressed() || Motion.GetDigitalInput(StartBtn1DIPin);
                        bool rightDebounce = Motion.IsTriggerRightPressed() || Motion.GetDigitalInput(StartBtn2DIPin);

                        if (leftDebounce && rightDebounce)
                        {
                            Log("[Sequence Trigger] Both safety trigger buttons pressed and confirmed -> Starting clamping cycle!");
                            triggered = true;
                            break;
                        }
                    }

                    await Task.Delay(10, ct);
                }

                if (!triggered) return false;
            }

            // STEP 3: Clamp Down (Hạ cơ cấu tỳ kẹp phẳng tệp sản phẩm, kiểm soát lực qua Loadcell Bongshin hoặc vị trí Teaching Point)
            SetState(SequenceState.ClampingDown);
            var teachPt = cfg?.TeachingPoints?.Find(p => p.TriggerVision || p.StepType == "CheckVision");
            double clampPos = teachPt != null && teachPt.Position > 0 ? teachPt.Position : (cfg?.ClampingPosition ?? (cfg != null && cfg.CapturePosition > 0 ? cfg.CapturePosition : 80.0));
            double clampSpeed = teachPt != null && teachPt.Speed > 0 ? teachPt.Speed : (cfg?.ClampingVelocity ?? 50.0);
            int dwellTime = teachPt != null && teachPt.DwellTimeMs > 0 ? teachPt.DwellTimeMs : (cfg?.ForceDwellTimeMs > 0 ? cfg.ForceDwellTimeMs : 150);
            int visionJobId = teachPt != null ? teachPt.JobId : 0;

            Log($"[Sequence] Clamping down to {clampPos:F2} mm (Speed {clampSpeed:F1} mm/s, monitoring Bongshin loadcell force)...");
            bool clampOk = await Motion.ClampDownAsync(clampPos, clampSpeed, ct);
            if (!clampOk)
            {
                Log("[Sequence Error] Clamping down command failed or timed out.");
                SetState(SequenceState.Error);
                return false;
            }

            // STEP 4: Force Dwell & Turn ON Backlight
            SetState(SequenceState.TriggeringVision);
            await Task.Delay(dwellTime, ct);

            // Turn ON Backlight for inspection
            if (cfg?.IO != null)
            {
                Motion.SetDigitalOutput((short)cfg.IO.BacklightDOBit, true);
            }

            // STEP 5: Vision Processing (Đếm số lượng 100 pcs & Kiểm tra ngược mặt)
            SetState(SequenceState.ProcessingVision);
            Log($"[Sequence Vision] Triggering camera & Cognex VisionPro processing (Job ID {visionJobId})...");

            bool visionOk = true;
            try
            {
                if (IsTestProgramMode)
                {
                    await Task.Delay(200, ct);
                    visionOk = string.Equals(MockVisionResult, "OK", StringComparison.OrdinalIgnoreCase);
                    Log($"[Sequence Test Program] Mock Vision Result = {MockVisionResult} (Success: {visionOk})");
                }
                else if (onVisionJobTrigger != null)
                {
                    visionOk = await onVisionJobTrigger(visionJobId);
                }
                else
                {
                    visionOk = await JobController.RunJobByIdAsync(visionJobId);
                }
            }
            catch (Exception ex)
            {
                Log($"[Vision Error] Job {visionJobId} execution failed: {ex.Message}");
                visionOk = false;
            }

            Log($"[Sequence Vision] Result: {(visionOk ? "PASS (OK - Count 100 & Correct Orientation)" : "FAIL (NG - Quantity Mismatch or Inverted)")}");

            // STEP 6: Unclamp & Retract Up (Nâng trục tỳ mở kẹp về vị trí chờ)
            SetState(SequenceState.UnclampingUp);
            // Turn OFF Backlight
            if (cfg?.IO != null)
            {
                Motion.SetDigitalOutput((short)cfg.IO.BacklightDOBit, false);
            }

            double retractSpeed = cfg?.RetractVelocity ?? 80.0;
            Log($"[Sequence] Retracting press axis to standby position (Speed {retractSpeed:F1} mm/s)...");
            bool retractOk = await Motion.RetractUpAsync(retractSpeed, ct);
            if (!retractOk)
            {
                Log("[Sequence Warning] Retract press axis warning or timeout.");
            }

            // STEP 7: Anti-tie-down protection: Ensure operator releases both buttons before allowing next cycle
            while (!ct.IsCancellationRequested &&
                   ((Motion.IsTriggerLeftPressed() || Motion.GetDigitalInput(StartBtn1DIPin)) ||
                    (Motion.IsTriggerRightPressed() || Motion.GetDigitalInput(StartBtn2DIPin))))
            {
                await Task.Delay(30, ct);
            }

            // STEP 8: Report & Tower Light Indication
            SetState(SequenceState.FinishingCycle);
            if (cfg?.IO != null)
            {
                if (visionOk)
                {
                    Motion.SetDigitalOutput((short)cfg.IO.TowerLightGreenDOBit, true);
                    Motion.SetDigitalOutput((short)cfg.IO.TowerLightRedDOBit, false);
                    Motion.SetDigitalOutput((short)cfg.IO.TowerBuzzerDOBit, false);
                }
                else
                {
                    Motion.SetDigitalOutput((short)cfg.IO.TowerLightGreenDOBit, false);
                    Motion.SetDigitalOutput((short)cfg.IO.TowerLightRedDOBit, true);
                    Motion.SetDigitalOutput((short)cfg.IO.TowerBuzzerDOBit, true);

                    // Auto turn off buzzer after 1 second
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        Motion.SetDigitalOutput((short)cfg.IO.TowerBuzzerDOBit, false);
                    });
                }
            }

            return visionOk;
        }

        public async Task<bool> MoveToPointAsync(TeachingPoint pt, CancellationToken ct = default)
        {
            if (pt == null || Motion == null) return false;
            Log($"[Teaching] Moving to point '{pt.Name}' ({pt.Position:F2} mm)...");
            bool ok = Motion.MoveAbsolute(pt.AxisIndex, pt.Position, pt.Speed, pt.Acceleration, pt.Acceleration);
            if (ok)
            {
                await Motion.WaitMoveDoneAsync(pt.AxisIndex, 30000, ct);
            }
            return ok;
        }

        public bool TeachCurrentPosition(int pointId, short axis = 0)
        {
            var cfg = Motion?.Config;
            var pt = cfg?.TeachingPoints?.Find(p => p.Id == pointId);
            if (pt == null) return false;

            var sts = Motion.GetAxisState(axis);
            pt.Position = Math.Round(sts.ActualPosition, 3);
            pt.AxisIndex = axis;
            Log($"[Teaching] Updated Position '{pt.Name}' = {pt.Position:F3} mm");
            return true;
        }

        public void StopCycle()
        {
            Log("[Sequence] Stop cycle requested by operator...");
            IsRunning = false;
            _cts?.Cancel();
            Motion.Stop(0);
            SetState(SequenceState.Stopped);
        }

        public void EmergencyStop()
        {
            Log("[Sequence CRITICAL] EMERGENCY STOP ACTIVATED!");
            IsRunning = false;
            _cts?.Cancel();
            Motion.EmergencyStop();
            SetState(SequenceState.Error);
        }

        private void SetState(SequenceState newState)
        {
            CurrentState = newState;
            OnStateChanged?.Invoke(newState);
        }

        private void Log(string msg)
        {
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }
    }
}

