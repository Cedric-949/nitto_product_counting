using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BeeMotionModule.Models;
using BeevisionSolution.Controller;
using BeevisionSolution.Jobs;
using BeevisionSolution.Utils;

namespace BeevisionSolution.ViewComponents
{
    public partial class MotionMainDashboardView : UserControl
    {
        private short _currentAxis = 0;
        private DispatcherTimer _ioPollingTimer;
        private bool _isAutoMode = false;
        private bool _isLightOn = false;
        private readonly System.Collections.Generic.Queue<string> _logLines = new System.Collections.Generic.Queue<string>();
        private const int MaxLogLines = 200;

        private static readonly SolidColorBrush TileOffBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
        private static readonly SolidColorBrush TileGreenBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41));
        private static readonly SolidColorBrush TileBlueBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
        private static readonly SolidColorBrush TileRedBrush = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        private static readonly SolidColorBrush TileYellowBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
        private static readonly SolidColorBrush TileDarkGrayBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

        // Màu trạng thái đồng bộ cho toàn bộ vùng Card I/O Signals khi Active (Tông xanh lá công nghiệp)
        private static readonly SolidColorBrush CardGreenBgBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x3E, 0x2B));
        private static readonly SolidColorBrush CardGreenBorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
        private static readonly SolidColorBrush CardGreenLedBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));

        private static readonly SolidColorBrush CardRedBgBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x15, 0x1B));
        private static readonly SolidColorBrush CardRedBorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x52));
        private static readonly SolidColorBrush CardRedLedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x52));

        private static readonly SolidColorBrush CardBlueBgBrush = CardGreenBgBrush;
        private static readonly SolidColorBrush CardBlueBorderBrush = CardGreenBorderBrush;
        private static readonly SolidColorBrush CardBlueLedBrush = CardGreenLedBrush;

        private static readonly SolidColorBrush CardYellowBgBrush = CardGreenBgBrush;
        private static readonly SolidColorBrush CardYellowBorderBrush = CardGreenBorderBrush;
        private static readonly SolidColorBrush CardYellowLedBrush = CardGreenLedBrush;

        public MotionMainDashboardView()
        {
            InitializeComponent();
            this.Loaded += MotionMainDashboardView_Loaded;
            this.Unloaded += MotionMainDashboardView_Unloaded;
        }

        private void MotionMainDashboardView_Loaded(object sender, RoutedEventArgs e)
        {
            MotionSequenceManager.Instance.AttachPCIeIO();

            var seq = MotionSequenceManager.Instance;
            if (seq.Motion != null)
            {
                seq.Motion.OnAxisStateUpdated += Motion_OnAxisStateUpdated;
                seq.Motion.OnLogMessage += Motion_OnLogMessage;
                seq.OnStateChanged += Seq_OnStateChanged;
                seq.OnCycleCompleted += Seq_OnCycleCompleted;
            }

            if (seq.Motion?.Config != null)
            {
                chkSimulate.IsChecked = seq.Motion.Config.Simulate;
            }

            // Đồng bộ trạng thái hiện tại của trục nếu đã kết nối
            if (seq.Motion != null && seq.Motion.IsConnected)
            {
                var sts = seq.Motion.GetAxisState(_currentAxis);
                if (sts != null)
                {
                    Motion_OnAxisStateUpdated(_currentAxis, sts);
                }
            }

            // Khởi chạy Timer quét tín hiệu IO và cảm biến chu kỳ 250ms
            if (_ioPollingTimer == null)
            {
                _ioPollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _ioPollingTimer.Tick += IoPollingTimer_Tick;
            }
            _ioPollingTimer.Start();
            IoPollingTimer_Tick(null, EventArgs.Empty);
            Motion_OnLogMessage("[System] Motion Dashboard ready.");
        }

        private void MotionMainDashboardView_Unloaded(object sender, RoutedEventArgs e)
        {
            var seq = MotionSequenceManager.Instance;
            if (seq.Motion != null)
            {
                seq.Motion.OnAxisStateUpdated -= Motion_OnAxisStateUpdated;
                seq.Motion.OnLogMessage -= Motion_OnLogMessage;
                seq.OnStateChanged -= Seq_OnStateChanged;
                seq.OnCycleCompleted -= Seq_OnCycleCompleted;
            }

            _ioPollingTimer?.Stop();
        }

        #region Event Handlers từ MotionSequenceManager & MotionController

        private void Motion_OnAxisStateUpdated(short axis, AxisState state)
        {
            if (axis != _currentAxis) return;

            Dispatcher.InvokeAsync(() =>
            {
                if (txtActualPos == null) return;

                txtActualPos.Text = $"{state.ActualPosition:F3} mm";
                txtActualVel.Text = $"{state.ActualVelocity:F1} mm/s";

                // Cập nhật các ô trạng thái (Tiles)
                tileSvOn.Background = state.IsServoOn ? TileGreenBrush : TileOffBrush;
                tileInp.Background = state.IsInPosition ? TileGreenBrush : TileOffBrush;
                tileHome.Background = state.IsHomed ? TileGreenBrush : TileOffBrush;
                tileLmPos.Background = state.LimitPositive ? TileRedBrush : TileOffBrush;
                tileLmNeg.Background = state.LimitNegative ? TileRedBrush : TileOffBrush;
                tileAlm.Background = state.IsError ? TileRedBrush : TileOffBrush;
                tileEmg.Background = state.EmergencyStop ? TileRedBrush : TileOffBrush;
                tileBusy.Background = state.IsBusy ? TileBlueBrush : TileOffBrush;

                // Nút Servo Toggle
                if (txtServoBtn != null)
                {
                    txtServoBtn.Text = state.IsServoOn ? "Servo OFF" : "Servo ON";
                    btnServoToggle.Background = state.IsServoOn ? TileRedBrush : TileGreenBrush;
                }

                // Cảnh báo lỗi
                uint currentErr = (uint)state.RawStatus;
                if (state.IsError)
                {
                    txtAlarmCode.Text = $"0x{currentErr:X4} (Alarm)";
                    txtAlarmCode.Foreground = TileRedBrush;
                }
                else
                {
                    txtAlarmCode.Text = "0x0000 (OK)";
                    txtAlarmCode.Foreground = TileGreenBrush;
                }
            });
        }

        private void Seq_OnStateChanged(SequenceState state)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (txtSeqStateBadge == null) return;
                txtSeqStateBadge.Text = state.ToString().ToUpper();
                txtSeqStateBadge.Foreground = state == SequenceState.Error ? TileRedBrush : (state == SequenceState.Idle ? new SolidColorBrush(Colors.LightGray) : TileGreenBrush);

                var normalBrush = TileDarkGrayBrush;
                var activeBrush = TileGreenBrush;

                if (cardStep1 != null) cardStep1.BorderBrush = normalBrush;
                if (cardStep2 != null) cardStep2.BorderBrush = normalBrush;
                if (cardStep3 != null) cardStep3.BorderBrush = normalBrush;
                if (cardStep4 != null) cardStep4.BorderBrush = normalBrush;

                switch (state)
                {
                    case SequenceState.CheckingReady:
                        if (cardStep1 != null) cardStep1.BorderBrush = activeBrush;
                        if (txtStep1Status != null) txtStep1Status.Text = "READY";
                        break;
                    case SequenceState.WaitingTrigger:
                        if (cardStep1 != null) cardStep1.BorderBrush = activeBrush;
                        if (txtStep1Status != null) txtStep1Status.Text = "WAITING";
                        break;
                    case SequenceState.ClampingDown:
                        if (cardStep1 != null) txtStep1Status.Text = "OK";
                        if (cardStep2 != null) cardStep2.BorderBrush = activeBrush;
                        if (txtStep2Status != null) txtStep2Status.Text = "CLAMPING";
                        break;
                    case SequenceState.TriggeringVision:
                        if (cardStep2 != null) txtStep2Status.Text = "FORCE HELD";
                        if (cardStep3 != null) cardStep3.BorderBrush = activeBrush;
                        if (txtStep3Status != null) txtStep3Status.Text = "STROBE";
                        break;
                    case SequenceState.ProcessingVision:
                        if (cardStep3 != null) cardStep3.BorderBrush = activeBrush;
                        if (txtStep3Status != null) txtStep3Status.Text = "COUNTING";
                        break;
                    case SequenceState.UnclampingUp:
                        if (cardStep3 != null) txtStep3Status.Text = "DONE";
                        if (cardStep4 != null) cardStep4.BorderBrush = activeBrush;
                        if (txtStep4Status != null) txtStep4Status.Text = "UNCLAMP";
                        break;
                    case SequenceState.FinishingCycle:
                        if (cardStep4 != null) txtStep4Status.Text = "FINISH";
                        break;
                    case SequenceState.Idle:
                        if (txtStep1Status != null) txtStep1Status.Text = "READY";
                        if (txtStep2Status != null) txtStep2Status.Text = "WAIT";
                        if (txtStep3Status != null) txtStep3Status.Text = "WAIT";
                        if (txtStep4Status != null) txtStep4Status.Text = "WAIT";
                        break;
                    case SequenceState.Error:
                        if (cardStep1 != null) cardStep1.BorderBrush = TileRedBrush;
                        if (cardStep2 != null) cardStep2.BorderBrush = TileRedBrush;
                        if (cardStep3 != null) cardStep3.BorderBrush = TileRedBrush;
                        if (cardStep4 != null) cardStep4.BorderBrush = TileRedBrush;
                        break;
                }
            });
        }

        private void Seq_OnCycleCompleted(int totalCount, double cycleTimeMs, bool success)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (txtSeqCycleCount != null) txtSeqCycleCount.Text = totalCount.ToString();
                if (txtSeqCycleTime != null) txtSeqCycleTime.Text = $"{(cycleTimeMs / 1000.0):F2} s";
            });
        }

        private void Motion_OnLogMessage(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;

            Dispatcher.InvokeAsync(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string line = $"[{timestamp}] {msg.Trim()}";
                _logLines.Enqueue(line);
                while (_logLines.Count > MaxLogLines)
                {
                    _logLines.Dequeue();
                }

                if (txtMotionLogs != null)
                {
                    txtMotionLogs.Text = string.Join(Environment.NewLine, _logLines);
                    txtMotionLogs.ScrollToEnd();
                }
            });
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            if (txtMotionLogs != null)
            {
                txtMotionLogs.Clear();
            }
        }

        private void IoPollingTimer_Tick(object sender, EventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;

            bool trigL = motion.IsTriggerLeftPressed();
            bool trigR = motion.IsTriggerRightPressed();
            bool forceReached = motion.IsForceTargetReached();
            bool homeUp = motion.IsHomeUpSensorActive();
            bool downLimit = motion.IsDownLimitSensorActive();
            bool partPresent = motion.IsPartPresent();

            // Status matrix tiles
            if (tileForce != null) tileForce.Background = forceReached ? TileGreenBrush : TileOffBrush;
            if (tilePart != null) tilePart.Background = partPresent ? TileGreenBrush : TileOffBrush;
            if (tileLight != null) tileLight.Background = _isLightOn ? TileGreenBrush : TileOffBrush;

            // Two-Hand Trigger IDEC indicators
            if (ledTriggerLeft != null) ledTriggerLeft.Fill = trigL ? TileGreenBrush : TileDarkGrayBrush;
            if (txtTriggerLeftStatus != null)
            {
                txtTriggerLeftStatus.Text = trigL ? "PRESSED (ACTIVE)" : "RELEASED (DI 1 / X02)";
                txtTriggerLeftStatus.Foreground = trigL ? TileGreenBrush : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }

            if (ledTriggerRight != null) ledTriggerRight.Fill = trigR ? TileGreenBrush : TileDarkGrayBrush;
            if (txtTriggerRightStatus != null)
            {
                txtTriggerRightStatus.Text = trigR ? "PRESSED (ACTIVE)" : "RELEASED (DI 2 / X03)";
                txtTriggerRightStatus.Foreground = trigR ? TileGreenBrush : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }

            // Force Sensor Loadcell Bongshin feedback
            if (ledForceReached != null) ledForceReached.Fill = forceReached ? TileGreenBrush : TileDarkGrayBrush;
            if (txtForceStatus != null)
            {
                txtForceStatus.Text = forceReached ? "Load Cell OK: ACTIVE (DI 10 / X11)" : "Load Cell OK: STANDBY (DI 10 / X11)";
                txtForceStatus.Foreground = forceReached ? TileGreenBrush : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }

            // Hiển thị các input chưa dùng và trạng thái output thực tế
            UpdateSignalCard(borderHomeUp, ledHomeUpSensor, homeUp, CardGreenBgBrush, CardGreenBorderBrush, CardGreenLedBrush);
            UpdateSignalCard(borderDownLimit, ledDownLimitSensor, downLimit, CardRedBgBrush, CardRedBorderBrush, CardRedLedBrush);
            UpdateSignalCard(borderPartPresent, ledPartPresentSensor, partPresent, CardBlueBgBrush, CardBlueBorderBrush, CardBlueLedBrush);

            bool brakeDO = motion.GetDigitalOutput((short)(motion.Config?.IO?.BrakeServoDOBit ?? 0));
            bool resetDO = motion.GetDigitalOutput((short)(motion.Config?.IO?.LoadCellResetDOBit ?? 3));
            bool holdDO = motion.GetDigitalOutput((short)(motion.Config?.IO?.LoadCellHoldDOBit ?? 4));

            UpdateSignalCard(borderTowerGreen, ledTowerGreen, brakeDO, CardGreenBgBrush, CardGreenBorderBrush, CardGreenLedBrush);
            UpdateSignalCard(borderTowerRed, ledTowerRed, resetDO, CardRedBgBrush, CardRedBorderBrush, CardRedLedBrush);
            UpdateSignalCard(borderBacklight, ledBacklight, holdDO, CardYellowBgBrush, CardYellowBorderBrush, CardYellowLedBrush);
        }

        /// <summary>
        /// Cập nhật hiển thị cho toàn bộ vùng Card của tín hiệu I/O.
        /// Khi Active: Cả Background và Border sáng lên tương ứng với màu trạng thái.
        /// Khi Inactive: Trở về màu nền tối và viền xám mặc định.
        /// </summary>
        private void UpdateSignalCard(Border border, Ellipse led, bool isActive, Brush activeBg, Brush activeBorder, Brush activeLed)
        {
            if (border != null)
            {
                border.Background = isActive ? activeBg : TileOffBrush;
                border.BorderBrush = isActive ? activeBorder : TileDarkGrayBrush;
            }

            if (led != null)
            {
                led.Fill = isActive ? activeLed : TileDarkGrayBrush;
            }
        }

        #endregion

        #region User Actions & Button Clicks

        private void CboAxisSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!this.IsLoaded || cboAxisSelect == null) return;
            _currentAxis = (short)Math.Max(0, cboAxisSelect.SelectedIndex);
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion != null)
            {
                var sts = motion.GetAxisState(_currentAxis);
                if (sts != null)
                {
                    Motion_OnAxisStateUpdated(_currentAxis, sts);
                }
            }
        }

        private void ChkSimulate_Changed(object sender, RoutedEventArgs e)
        {
            var cfg = MotionSequenceManager.Instance.Motion?.Config ?? new MotionConfig();
            cfg.Simulate = chkSimulate.IsChecked == true;
        }

        private async void BtnClampDown_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;
            btnClampDown.IsEnabled = false;
            try
            {
                Motion_OnLogMessage("[Manual] Clamping down product (Monitoring Bongshin loadcell force)...");
                await motion.ClampDownAsync();
            }
            finally
            {
                btnClampDown.IsEnabled = true;
            }
        }

        private async void BtnRetractUp_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;
            btnRetractUp.IsEnabled = false;
            try
            {
                Motion_OnLogMessage("[Manual] Retracting press axis to standby open position (0 mm)...");
                await motion.RetractUpAsync();
            }
            finally
            {
                btnRetractUp.IsEnabled = true;
            }
        }

        private void BtnAutoMode_Click(object sender, RoutedEventArgs e)
        {
            _isAutoMode = true;
            btnAutoMode.Background = TileGreenBrush;
            btnManualMode.Background = TileOffBrush;
            txtModeStatus.Text = "AUTO";
            txtModeStatus.Foreground = TileGreenBrush;

            // Kích hoạt chu trình Auto
            MotionSequenceManager.Instance.StartCycleAsync(true);
        }

        private void BtnManualMode_Click(object sender, RoutedEventArgs e)
        {
            _isAutoMode = false;
            btnManualMode.Background = TileGreenBrush;
            btnAutoMode.Background = TileOffBrush;
            txtModeStatus.Text = "MANUAL";
            txtModeStatus.Foreground = TileGreenBrush;

            MotionSequenceManager.Instance.StopCycle();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            Motion_OnLogMessage("[Manual] Press STOP -> Emergency stop for all motion cycles.");
            MotionSequenceManager.Instance.StopCycle();
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion != null)
            {
                for (short i = 0; i < (motion.Config?.TotalAxes ?? 1); i++)
                {
                    motion.Stop(i);
                }
            }
        }

        private async void BtnHome_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;

            var sts = motion.GetAxisState(_currentAxis);
            if (sts == null || !sts.IsServoOn)
            {
                Motion_OnLogMessage($"[Manual Warn] Axis {_currentAxis}: Cannot Move to Home because Servo is OFF. Please Turn Servo ON first.");
                return;
            }

            btnHome.IsEnabled = false;
            try
            {
                double targetPos = motion.Config?.StandbyPosition ?? 0.0;
                double speed = motion.Config?.RetractVelocity ?? 50.0;
                if (speed <= 0) speed = 50.0;

                Motion_OnLogMessage($"[Manual] Moving Axis {_currentAxis} to Home position ({targetPos:F3} mm) at {speed:F1} mm/s...");
                bool ok = motion.MoveAbsolute(_currentAxis, targetPos, speed);
                if (ok)
                {
                    await motion.WaitMoveDoneAsync(_currentAxis, 20000);
                    Motion_OnLogMessage($"[Manual] Axis {_currentAxis} reached Home position ({targetPos:F3} mm).");
                }
                else
                {
                    Motion_OnLogMessage($"[Manual Warn] Axis {_currentAxis}: Move to Home failed!");
                }
            }
            catch (Exception ex)
            {
                Motion_OnLogMessage($"[Manual Exception] Move to Home error: {ex.Message}");
            }
            finally
            {
                btnHome.IsEnabled = true;
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            Motion_OnLogMessage($"[Manual] Reset Axis {_currentAxis} & Clear Alarm");
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion != null)
            {
                motion.ClearAlarm(_currentAxis);
                motion.Stop(_currentAxis);
            }
            txtAlarmCode.Text = "0x0000 (Reset)";
            txtAlarmCode.Foreground = TileGreenBrush;
        }

        private void BtnClearAlm_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion != null)
            {
                motion.ClearAlarm(_currentAxis);
                Motion_OnLogMessage($"[Manual] Clear Alarm Axis {_currentAxis}");
            }
        }

        private async void BtnServoToggle_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;

            var sts = motion.GetAxisState(_currentAxis);
            if (sts == null) return;

            if (sts.IsServoOn)
            {
                // Khi tắt Servo: Tắt Servo rồi đóng lại phanh cơ (tắt DO)
                motion.ServoOff(_currentAxis);
                MotionSequenceManager.Instance.SetDigitalOutput(MotionSequenceManager.Instance.BrakeDOPin, false);
                Motion_OnLogMessage($"[Manual] Axis {_currentAxis}: Servo OFF & Brake Locked.");
            }
            else
            {
                // Khi bật Servo: Phải đi qua chuỗi Nhả phanh PCIe -> Tắt Emergency -> Servo ON
                Motion_OnLogMessage($"[Manual] Axis {_currentAxis}: Enabling Servo (Release Brake -> Clear Emg -> Servo ON)...");
                bool ok = await MotionSequenceManager.Instance.EnableServoSequenceAsync(_currentAxis);
                if (!ok)
                {
                    Motion_OnLogMessage($"[Manual] Axis {_currentAxis}: Failed to enable servo.");
                }
            }
        }

        #endregion
    }
}
