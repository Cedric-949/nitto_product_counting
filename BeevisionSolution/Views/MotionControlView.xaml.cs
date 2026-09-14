using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AForge.Math.Metrics;
using BeeMotionModule.Models;
using BeevisionSolution.Controller;
using BeevisionSolution.Jobs;
using BeevisionSolution.Utils;

namespace BeevisionSolution.Views
{
    public class AlarmRecord
    {
        public int Id { get; set; }
        public DateTime Time { get; set; }
        public string TimeFormatted => Time.ToString("yyyy-MM-dd HH:mm:ss");
        public string AxisName { get; set; } = "Axis 0";
        public uint ErrorCode { get; set; }
        public string CodeHex => $"0x{ErrorCode:X4}";
        public string Message { get; set; } = string.Empty;
        public string StatusText { get; set; } = "ACTIVE";
    }

    public partial class MotionControlView : UserControl
    {
        private short _currentAxis = 0;
        private readonly Queue<string> _logLines = new Queue<string>();
        private const int MaxLogLines = 200;

        public ObservableCollection<IoPinDisplayItem> DiItems { get; set; } = new ObservableCollection<IoPinDisplayItem>();
        public ObservableCollection<IoPinDisplayItem> DoItems { get; set; } = new ObservableCollection<IoPinDisplayItem>();


        private static readonly SolidColorBrush TileOffBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
        private static readonly SolidColorBrush TileGreenBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57));
        private static readonly SolidColorBrush TileBlueBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
        private static readonly SolidColorBrush TileRedBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x14, 0x3C));
        private static readonly SolidColorBrush TileOrangeBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
        private static readonly SolidColorBrush TileYellowBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xAF, 0x37));

        private readonly ObservableCollection<TeachingPoint> _teachingPoints = new ObservableCollection<TeachingPoint>();
        private readonly ObservableCollection<AlarmRecord> _alarmHistory = new ObservableCollection<AlarmRecord>();
        private uint _lastErrorCode = 0;
        private bool _wasInError = false;

        private DispatcherTimer _ioPollingTimer;
        private bool _isAutoMode = false;
        private bool _isLightOn = false;

        #region Fallback Controls (Migrated to MotionMainDashboardView)
        private readonly Border tileSvOn = null;
        private readonly Border tileInp = null;
        private readonly Border tileAlm = null;
        private readonly Border tileEmg = null;
        private readonly Border tileBusy = null;

        private readonly TextBlock txtSeqStateBadge = null;
        private readonly TextBlock txtSeqCycleCount = null;
        private readonly TextBlock txtSeqCycleTime = null;

        private readonly Border cardStep1 = null;
        private readonly Border cardStep2 = null;
        private readonly Border cardStep3 = null;
        private readonly Border cardStep4 = null;

        private readonly TextBlock txtStep1Status = null;
        private readonly TextBlock txtStep2Status = null;
        private readonly TextBlock txtStep3Status = null;
        private readonly TextBlock txtStep4Status = null;

        private readonly Border tileForce = null;
        private readonly Border tilePart = null;
        private readonly Border tileLight = null;

        private readonly System.Windows.Shapes.Ellipse ledTriggerLeft = null;
        private readonly TextBlock txtTriggerLeftStatus = null;
        private readonly System.Windows.Shapes.Ellipse ledTriggerRight = null;
        private readonly TextBlock txtTriggerRightStatus = null;
        private readonly System.Windows.Shapes.Ellipse ledForceReached = null;
        private readonly TextBlock txtForceStatus = null;
        private readonly System.Windows.Shapes.Ellipse ledHomeUpSensor = null;
        private readonly System.Windows.Shapes.Ellipse ledDownLimitSensor = null;
        private readonly System.Windows.Shapes.Ellipse ledPartPresentSensor = null;

        private readonly System.Windows.Shapes.Ellipse ledTowerGreen = null;
        private readonly System.Windows.Shapes.Ellipse ledTowerRed = null;
        private readonly System.Windows.Shapes.Ellipse ledBacklight = null;

        private readonly Button btnAutoMode = null;
        private readonly Button btnManualMode = null;
        private readonly TextBlock txtModeStatus = null;
        private readonly Button btnClampDown = null;
        private readonly Button btnRetractUp = null;

        private readonly TextBox txtMotionLogs = null;
        #endregion

        public MotionControlView()
        {
            InitializeComponent();
            this.Loaded += MotionControlView_Loaded;
            this.Unloaded += MotionControlView_Unloaded;
        }

        private void MotionControlView_Loaded(object sender, RoutedEventArgs e)
        {
            MotionSequenceManager.Instance.AttachPCIeIO();
            InitIoList();
            icDigitalInputs.ItemsSource = DiItems;
            icDigitalOutputs.ItemsSource = DoItems;

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

            dgTeachingPoints.ItemsSource = _teachingPoints;
            dgAlarmHistory.ItemsSource = _alarmHistory;

            LoadTeachingPoints();
            LoadMotionConfigToUI();
            UpdateMasterStatusUI();

            // IO Polling Timer (250ms)
            if (_ioPollingTimer == null)
            {
                _ioPollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _ioPollingTimer.Tick += IoPollingTimer_Tick;
            }
            _ioPollingTimer.Start();
        }

        private void MotionControlView_Unloaded(object sender, RoutedEventArgs e)
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

        #region Axis Selection & Real-time Status
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
            LoadMotionConfigToUI();
        }

        private void RbJogMode_Changed(object sender, RoutedEventArgs e)
        {
            if (cboStepDistance == null) return;
            cboStepDistance.Visibility = (rbJogStep?.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChkSimulate_Changed(object sender, RoutedEventArgs e)
        {
            var cfg = MotionSequenceManager.Instance.Motion?.Config ?? new MotionConfig();
            cfg.Simulate = chkSimulate.IsChecked == true;
        }

        private void Motion_OnAxisStateUpdated(short axis, AxisState state)
        {
            if (axis != _currentAxis) return;

            Dispatcher.InvokeAsync(() =>
            {
                if (txtActualPos == null) return;

                txtActualPos.Text = $"{state.ActualPosition:F3} mm";
                txtActualVel.Text = $"{state.ActualVelocity:F1} mm/s";

                // Update Status Matrix Tiles
                if (tileSvOn != null) tileSvOn.Background = state.IsServoOn ? TileGreenBrush : TileOffBrush;
                if (tileInp != null) tileInp.Background = state.IsInPosition ? TileBlueBrush : TileOffBrush;

                if (tileHome != null)
                {
                    tileHome.Background = state.HomeSensor ? TileGreenBrush : TileOffBrush;
                    tileHome.BorderBrush = state.HomeSensor ? TileGreenBrush : (state.IsHomed ? TileBlueBrush : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)));
                }
                if (ledHome != null)
                {
                    ledHome.Fill = state.HomeSensor ? TileGreenBrush : (state.IsHomed ? TileBlueBrush : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));
                }

                if (tileLmPos != null)
                {
                    tileLmPos.Background = state.LimitPositive ? TileRedBrush : TileOffBrush;
                    tileLmPos.BorderBrush = state.LimitPositive ? TileRedBrush : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
                }
                if (ledLmPos != null)
                {
                    ledLmPos.Fill = state.LimitPositive ? TileRedBrush : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                }

                if (tileLmNeg != null)
                {
                    tileLmNeg.Background = state.LimitNegative ? TileRedBrush : TileOffBrush;
                    tileLmNeg.BorderBrush = state.LimitNegative ? TileRedBrush : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
                }
                if (ledLmNeg != null)
                {
                    ledLmNeg.Fill = state.LimitNegative ? TileRedBrush : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                }

                if (tileAlm != null) tileAlm.Background = state.IsError ? TileRedBrush : TileOffBrush;
                if (tileEmg != null) tileEmg.Background = state.EmergencyStop ? TileRedBrush : TileOffBrush;
                if (tileBusy != null) tileBusy.Background = state.IsBusy ? TileYellowBrush : TileOffBrush;

                // Update Servo Button Text
                if (btnServoToggle != null)
                {
                    btnServoToggle.Content = state.IsServoOn ? "Servo OFF" : "Servo ON";
                    btnServoToggle.Background = state.IsServoOn ? TileRedBrush : TileGreenBrush;
                }

                // Alarm detection
                uint currentErr = (uint)state.RawStatus;
                if (state.IsError)
                {
                    if (txtAlarmCode != null)
                    {
                        txtAlarmCode.Text = $"0x{currentErr:X4} (Axis Error)";
                        txtAlarmCode.Foreground = TileRedBrush;
                    }

                    if (!_wasInError || currentErr != _lastErrorCode)
                    {
                        _alarmHistory.Insert(0, new AlarmRecord
                        {
                            Id = _alarmHistory.Count + 1,
                            Time = DateTime.Now,
                            AxisName = $"Axis {axis}",
                            ErrorCode = currentErr,
                            Message = "Hardware Drive Alarm or Following Error triggered.",
                            StatusText = "ACTIVE"
                        });
                        while (_alarmHistory.Count > MaxLogLines)
                        {
                            _alarmHistory.RemoveAt(_alarmHistory.Count - 1);
                        }
                    }
                    _wasInError = true;
                    _lastErrorCode = currentErr;
                }
                else
                {
                    if (txtAlarmCode != null)
                    {
                        txtAlarmCode.Text = "0x0000 (Normal / No Error)";
                        txtAlarmCode.Foreground = TileGreenBrush;
                    }
                    _wasInError = false;
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

                var normalBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
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
                        if (txtStep4Status != null) txtStep4Status.Text = "RETRACTING";
                        break;
                    case SequenceState.FinishingCycle:
                        if (cardStep4 != null) cardStep4.BorderBrush = activeBrush;
                        if (txtStep4Status != null) txtStep4Status.Text = "FINISHED";
                        break;
                    case SequenceState.Idle:
                        if (txtStep1Status != null) txtStep1Status.Text = "READY";
                        if (txtStep2Status != null) txtStep2Status.Text = "WAIT";
                        if (txtStep3Status != null) txtStep3Status.Text = "WAIT";
                        if (txtStep4Status != null) txtStep4Status.Text = "WAIT";
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

            // 1. Kiểm tra xem có Card PCIe IO rời hay không
            var pcieIo = IoJobCtrl.GetIOcardCtrl();
            bool hasPcie = pcieIo != null && pcieIo.IsInit;

            if (txtIoCardStatus != null)
            {
                if (hasPcie)
                {
                    txtIoCardStatus.Text = $"PCIe Ready ({pcieIo.Name}, In={pcieIo.InputChannels}, Out={pcieIo.OutputChannels})";
                    txtIoCardStatus.Foreground = TileGreenBrush;
                }
                else
                {
                    txtIoCardStatus.Text = "PCIe Not Initialized (Fallback: Inovance EtherCAT IO)";
                    txtIoCardStatus.Foreground = TileOrangeBrush;
                }
            }

            if (hasPcie)
            {
                // Cập nhật trạng thái DI từ Card PCIe theo 0-based channel
                for (int i = 0; i < DiItems.Count; i++)
                {
                    int ch = DiItems[i].Pin;
                    if (ch >= 0 && ch < pcieIo.InputChannels)
                    {
                        DiItems[i].State = pcieIo.GetChannelInput(ch);
                    }
                }

                // Cập nhật trạng thái DO từ Card PCIe theo 0-based channel
                for (int i = 0; i < DoItems.Count; i++)
                {
                    int ch = DoItems[i].Pin;
                    if (ch >= 0 && ch < pcieIo.OutputChannels)
                    {
                        DoItems[i].State = pcieIo.GetChannelOutput(ch);
                    }
                }
            }
            else
            {
                // Fallback đọc từ Card Inovance nếu Card PCIe chưa bật
                for (int i = 0; i < DiItems.Count; i++)
                {
                    DiItems[i].State = motion.GetDigitalInput((short)DiItems[i].Pin);
                }
                for (int i = 0; i < DoItems.Count; i++)
                {
                    DoItems[i].State = motion.GetDigitalOutput((short)DoItems[i].Pin);
                }
            }

            // Status matrix tiles
            if (tileForce != null) tileForce.Background = forceReached ? TileGreenBrush : TileOffBrush;
            if (tilePart != null) tilePart.Background = partPresent ? TileBlueBrush : TileOffBrush;
            if (tileLight != null) tileLight.Background = _isLightOn ? TileGreenBrush : TileOffBrush;

            // Two-Hand Trigger IDEC indicators
            if (ledTriggerLeft != null) ledTriggerLeft.Fill = trigL ? TileGreenBrush : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
            if (txtTriggerLeftStatus != null)
            {
                txtTriggerLeftStatus.Text = trigL ? "PRESSED (ACTIVE)" : "RELEASED (DI 1 / X02)";
                txtTriggerLeftStatus.Foreground = trigL ? TileGreenBrush : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }

            if (ledTriggerRight != null) ledTriggerRight.Fill = trigR ? TileGreenBrush : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
            if (txtTriggerRightStatus != null)
            {
                txtTriggerRightStatus.Text = trigR ? "PRESSED (ACTIVE)" : "RELEASED (DI 2 / X03)";
                txtTriggerRightStatus.Foreground = trigR ? TileGreenBrush : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }

            // Force Sensor Loadcell Bongshin feedback
            if (ledForceReached != null) ledForceReached.Fill = forceReached ? TileGreenBrush : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
            if (txtForceStatus != null)
            {
                txtForceStatus.Text = forceReached ? "Load Cell OK: ACTIVE (DI 10 / X11)" : "Load Cell OK: STANDBY (DI 10 / X11)";
                txtForceStatus.Foreground = forceReached ? TileGreenBrush : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }

            // Misumi Optical Sensors
            if (ledHomeUpSensor != null) ledHomeUpSensor.Fill = homeUp ? TileGreenBrush : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
            if (ledDownLimitSensor != null) ledDownLimitSensor.Fill = downLimit ? TileRedBrush : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
            if (ledPartPresentSensor != null) ledPartPresentSensor.Fill = partPresent ? TileBlueBrush : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

            bool brakeDO = motion.GetDigitalOutput((short)(motion.Config?.IO?.BrakeServoDOBit ?? 0));
            bool resetDO = motion.GetDigitalOutput((short)(motion.Config?.IO?.LoadCellResetDOBit ?? 3));
            bool holdDO = motion.GetDigitalOutput((short)(motion.Config?.IO?.LoadCellHoldDOBit ?? 4));

            if (ledTowerGreen != null) ledTowerGreen.Fill = brakeDO ? TileGreenBrush : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
            if (ledTowerRed != null) ledTowerRed.Fill = resetDO ? TileRedBrush : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
            if (ledBacklight != null) ledBacklight.Fill = holdDO ? TileYellowBrush : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        }
    

            #endregion

            #region Bottom Action Bar Handlers (Like MotionVision)
        private void BtnAutoMode_Click(object sender, RoutedEventArgs e)
        {
            _isAutoMode = true;
            if (btnAutoMode != null) btnAutoMode.Background = TileGreenBrush;
            if (btnManualMode != null) btnManualMode.Background = TileOffBrush;
            if (txtModeStatus != null)
            {
                txtModeStatus.Text = "AUTO";
                txtModeStatus.Foreground = TileGreenBrush;
            }

            // Start Cycle in Auto Mode
            MotionSequenceManager.Instance.StartCycleAsync(true);
        }

        private void BtnManualMode_Click(object sender, RoutedEventArgs e)
        {
            _isAutoMode = false;
            if (btnManualMode != null) btnManualMode.Background = TileGreenBrush;
            if (btnAutoMode != null) btnAutoMode.Background = TileOffBrush;
            if (txtModeStatus != null)
            {
                txtModeStatus.Text = "MANUAL";
                txtModeStatus.Foreground = TileGreenBrush;
            }

            MotionSequenceManager.Instance.StopCycle();
        }

        private async void BtnGoHome_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;

            var sts = motion.GetAxisState(_currentAxis);
            if (sts == null || !sts.IsServoOn)
            {
                Motion_OnLogMessage($"[Manual Warn] Axis {_currentAxis}: Cannot Move to Home because Servo is OFF. Please Turn Servo ON first.");
                return;
            }

            if (btnGoHome != null) btnGoHome.IsEnabled = false;
            try
            {
                double targetPos = motion.Config?.StandbyPosition ?? 0.0;
                double speed = motion.Config?.RetractVelocity ?? 50.0;
                if (speed <= 0) speed = 50.0;

                Motion_OnLogMessage($"[Manual] Axis {_currentAxis}: Moving to Home position ({targetPos:F3} mm) at {speed:F1} mm/s...");
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
                if (btnGoHome != null) btnGoHome.IsEnabled = true;
            }
        }

        private void BtnSetHome_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;

            bool ok = motion.SetZero(_currentAxis);
            if (ok)
            {
                Motion_OnLogMessage($"[Setting] Axis {_currentAxis}: Current coordinate set to 0.000 mm for this session. Axis is not hardware-homed.");
                if (txtActualPos != null) txtActualPos.Text = "0.000 mm";
            }
            else
            {
                Motion_OnLogMessage($"[Setting Error] Axis {_currentAxis}: Set Home (Set Zero) failed!");
            }
        }

        private async void BtnRunHoming_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;

            var sts = motion.GetAxisState(_currentAxis);
            if (sts == null || !sts.IsServoOn)
            {
                Motion_OnLogMessage($"[Advanced Warn] Axis {_currentAxis}: Cannot run Homing because Servo is OFF. Please Turn Servo ON first.");
                return;
            }

            if (btnRunHoming != null) btnRunHoming.IsEnabled = false;
            try
            {
                Motion_OnLogMessage($"[Advanced] Axis {_currentAxis}: Executing Hardware Homing routine...");
                bool ok = await motion.HomeAsync(_currentAxis);
                Motion_OnLogMessage($"[Advanced] Axis {_currentAxis}: Homing routine result: {(ok ? "SUCCESS" : "FAILED")}");
            }
            catch (Exception ex)
            {
                Motion_OnLogMessage($"[Advanced Exception] Homing routine error: {ex.Message}");
            }
            finally
            {
                if (btnRunHoming != null) btnRunHoming.IsEnabled = true;
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            Motion_OnLogMessage($"[Manual] Press RESET -> Clear Alarm and stop Axis {_currentAxis}");
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion != null)
            {
                motion.ClearAlarm(_currentAxis);
                motion.Stop(_currentAxis);
            }
            if (txtAlarmCode != null)
            {
                txtAlarmCode.Text = "0x0000 (Reset)";
                txtAlarmCode.Foreground = TileGreenBrush;
            }
            Motion_OnLogMessage("[System] Reset command executed.");
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
                    Motion_OnLogMessage($"[Manual Alarm] Axis {_currentAxis}: Enable Servo Failed!");
                }
            }
        }


        private async void BtnTriggerCam_Click(object sender, RoutedEventArgs e)
        {
            Motion_OnLogMessage("[Vision] Manual Camera Trigger...");
            await JobController.RunJobByIdAsync(0);
        }
        #endregion

        #region Manual Jog & Move
        private void BtnJogPos_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            StartJog(1);
        }

        private void BtnJogPos_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (rbJogContinuous.IsChecked == true) StopJog();
        }

        private void BtnJogNeg_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            StartJog(-1);
        }

        private void BtnJogNeg_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (rbJogContinuous.IsChecked == true) StopJog();
        }

        private void StartJog(int direction)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;

            var sts = motion.GetAxisState(_currentAxis);
            if (sts != null)
            {
                if (!sts.IsServoOn)
                {
                    Motion_OnLogMessage($"[Manual Warn] Axis {_currentAxis}: Cannot JOG because Servo is OFF. Please Turn Servo ON first.");
                    return;
                }

                // Cảnh báo nếu bấm hướng tiếp tục đâm vào Limit cứng đang chạm
                if (sts.LimitNegative && direction < 0)
                {
                    Motion_OnLogMessage($"[Manual Block] Axis {_currentAxis} is hitting LIMIT - (Down)! Negative move blocked by hardware. Use JOG + (UP) to move away.");
                    return;
                }
                if (sts.LimitPositive && direction > 0)
                {
                    Motion_OnLogMessage($"[Manual Block] Axis {_currentAxis} is hitting LIMIT + (Up)! Positive move blocked by hardware. Use JOG - (Down) to move away.");
                    return;
                }

            }

            if (!double.TryParse(txtJogSpeed.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double speed) || speed <= 0)
            {
                speed = 10;
            }

            if (rbJogStep.IsChecked == true)
            {
                double stepDist = 1.0;
                if (cboStepDistance.SelectedItem is ComboBoxItem item)
                {
                    string str = item.Content.ToString().Replace("mm", "").Trim();
                    double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out stepDist);
                }
                Motion_OnLogMessage($"[Manual Move] Press JOG {(direction > 0 ? "+ (UP)" : "- (Down)")} {stepDist} mm | Speed: {speed} mm/s (Axis {_currentAxis})");
                motion.MoveRelative(_currentAxis, direction * stepDist, speed);
            }
            else
            {
                Motion_OnLogMessage($"[Manual] Press JOG {(direction > 0 ? "+ (UP)" : "- (Down)")} Continuous | Speed: {speed} mm/s (Axis {_currentAxis})");
                motion.MoveJog(_currentAxis, direction * speed);
            }
        }

        private void StopJog()
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;
            Motion_OnLogMessage($"[Manual] Threw JOG -> Stop Axis {_currentAxis}");
            motion.Stop(_currentAxis);
        }

        private void BtnMoveAbs_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;

            if (double.TryParse(txtTargetPos.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double targetPos))
            {
                double.TryParse(txtJogSpeed.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double speed);
                if (speed <= 0) speed = 10;
                Motion_OnLogMessage($"[Manual] Press ABS MOVE -> Move to Abs Pos: {targetPos:F3} mm | Speed: {speed} mm/s (Axis {_currentAxis})");
                motion.MoveAbsolute(_currentAxis, targetPos, speed);
            }
        }

        private void BtnMoveRel_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;

            if (double.TryParse(txtTargetPos.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double dist))
            {
                double.TryParse(txtJogSpeed.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double speed);
                if (speed <= 0) speed = 10;
                Motion_OnLogMessage($"[Manual] Press REL MOVE -> Move relative: {dist:F3} mm | Speed: {speed} mm/s (Axis {_currentAxis})");
                motion.MoveRelative(_currentAxis, dist, speed);
            }
        }
        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            Motion_OnLogMessage($"[Manual] Press Stop -> STOP ALL");
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

        private async void BtnClampDown_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;
            if (btnClampDown != null) btnClampDown.IsEnabled = false;
            if (btnQuickClamp != null) btnQuickClamp.IsEnabled = false;
            try
            {
                Motion_OnLogMessage("[Manual] Clamping down product (Monitoring Bongshin loadcell force)...");
                await motion.ClampDownAsync();
            }
            finally
            {
                if (btnClampDown != null) btnClampDown.IsEnabled = true;
                if (btnQuickClamp != null) btnQuickClamp.IsEnabled = true;
            }
        }

        private async void BtnRetractUp_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;
            if (btnRetractUp != null) btnRetractUp.IsEnabled = false;
            if (btnQuickRetract != null) btnQuickRetract.IsEnabled = false;
            try
            {
                Motion_OnLogMessage("[Manual] Retracting press axis to standby open position (0 mm)...");
                await motion.RetractUpAsync();
            }
            finally
            {
                if (btnRetractUp != null) btnRetractUp.IsEnabled = true;
                if (btnQuickRetract != null) btnQuickRetract.IsEnabled = true;
            }
        }

        private async void BtnSimulateTrigger_Click(object sender, RoutedEventArgs e)
        {
            Motion_OnLogMessage("[Simulate] Simulating IDEC YW1L two-hand safety trigger buttons...");
            await MotionSequenceManager.Instance.SimulateTriggerAsync();
        }

        private void BtnLightToggle_Click(object sender, RoutedEventArgs e)
        {
            _isLightOn = !_isLightOn;
            try
            {
                var lights = JobController.GetAllLights();
                if (lights != null && lights.Count > 0)
                {
                    foreach (var light in lights)
                    {
                        if (_isLightOn)
                        {
                            light.LightOn(1, 200);
                        }
                        else
                        {
                            light.LightOff(1);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Motion_OnLogMessage($"[Light Error] {ex.Message}");
            }

            if (tileLight != null)
            {
                tileLight.Background = _isLightOn ? TileGreenBrush : TileOffBrush;
            }
            Motion_OnLogMessage($"[Light] All Light Controllers {(_isLightOn ? "Turned ON" : "Turned OFF")}");
        }
        #endregion

        #region Teaching Points
        private void LoadTeachingPoints(MotionConfig specificConfig = null)
        {
            _teachingPoints.Clear();
            var cfg = specificConfig ?? MotionSequenceManager.Instance.Motion?.Config;
            if (cfg == null && File.Exists(Common.MotionConfigFile))
            {
                try
                {
                    string json = File.ReadAllText(Common.MotionConfigFile);
                    cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<MotionConfig>(json, Common.JsonPrivate);
                    if (cfg != null && MotionSequenceManager.Instance.Motion != null)
                    {
                        MotionSequenceManager.Instance.Motion.Config = cfg;
                    }
                }
                catch (Exception ex)
                {
                    Motion_OnLogMessage($"[Teaching Error] Failed to read motion_config.json: {ex.Message}");
                }
            }

            if (cfg != null)
            {
                if (cfg.TeachingPoints != null && cfg.TeachingPoints.Count > 0)
                {
                    foreach (var pt in cfg.TeachingPoints)
                    {
                        _teachingPoints.Add(pt);
                    }
                }
                else
                {
                    cfg.EnsureDefaultTeachingPoints();
                    if (cfg.TeachingPoints != null)
                    {
                        foreach (var pt in cfg.TeachingPoints)
                        {
                            _teachingPoints.Add(pt);
                        }
                    }
                }
            }
        }

        private TeachingPoint GetSelectedTeachingPoint()
        {
            if (dgTeachingPoints.SelectedItem is TeachingPoint pt)
                return pt;

            if (dgTeachingPoints.CurrentItem is TeachingPoint curPt)
                return curPt;

            if (dgTeachingPoints.SelectedCells != null && dgTeachingPoints.SelectedCells.Count > 0)
            {
                var cell = dgTeachingPoints.SelectedCells[0];
                if (cell.Item is TeachingPoint cellPt)
                    return cellPt;
            }

            if (dgTeachingPoints.SelectedIndex >= 0 && dgTeachingPoints.SelectedIndex < _teachingPoints.Count)
            {
                return _teachingPoints[dgTeachingPoints.SelectedIndex];
            }

            return null;
        }

        private void BtnTeachCurrent_Click(object sender, RoutedEventArgs e)
        {
            var selectedPt = GetSelectedTeachingPoint();
            if (selectedPt != null)
            {
                var sts = MotionSequenceManager.Instance.Motion?.GetAxisState(_currentAxis);
                if (sts != null)
                {
                    selectedPt.Position = Math.Round(sts.ActualPosition, 3);
                    selectedPt.AxisIndex = _currentAxis;
                    dgTeachingPoints.Items.Refresh();
                    Motion_OnLogMessage($"[Teaching] Point '{selectedPt.Name}' position updated to {selectedPt.Position:F3} mm");
                }
            }
            else
            {
                MessageBox.Show("Please select a teaching point in the table to update position.", "Nitto Motion", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnAddPoint_Click(object sender, RoutedEventArgs e)
        {
            int nextId = _teachingPoints.Count > 0 ? _teachingPoints.Max(p => p.Id) + 1 : 1;
            var sts = MotionSequenceManager.Instance.Motion?.GetAxisState(_currentAxis);
            double curPos = sts != null ? Math.Round(sts.ActualPosition, 3) : 0.0;

            var newPt = new TeachingPoint
            {
                Id = nextId,
                Name = $"Point {nextId}",
                AxisIndex = _currentAxis,
                Position = curPos,
                Speed = 100,
                StepType = "CheckVision",
                StepOrder = nextId,
                TriggerVision = false
            };
            _teachingPoints.Add(newPt);
            dgTeachingPoints.SelectedItem = newPt;
            dgTeachingPoints.ScrollIntoView(newPt);
            Motion_OnLogMessage($"[Teaching] Added new point '{newPt.Name}' (ID {newPt.Id}).");
        }

        private void BtnDeletePoint_Click(object sender, RoutedEventArgs e)
        {
            dgTeachingPoints.CommitEdit(DataGridEditingUnit.Cell, true);
            dgTeachingPoints.CommitEdit(DataGridEditingUnit.Row, true);

            var selectedPt = GetSelectedTeachingPoint();
            if (selectedPt != null)
            {
                string ptName = selectedPt.Name;
                int ptId = selectedPt.Id;
                _teachingPoints.Remove(selectedPt);
                dgTeachingPoints.Items.Refresh();
                Motion_OnLogMessage($"[Teaching] Deleted point '{ptName}' (ID {ptId}).");
            }
            else
            {
                MessageBox.Show("Please select a teaching point in the table to delete.", "Nitto Motion", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnSavePoints_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. Kết thúc chỉnh sửa ô / dòng trong DataGrid trước khi lấy dữ liệu
                dgTeachingPoints.CommitEdit(DataGridEditingUnit.Cell, true);
                dgTeachingPoints.CommitEdit(DataGridEditingUnit.Row, true);

                var motion = MotionSequenceManager.Instance.Motion;
                var cfg = motion?.Config ?? new MotionConfig();
                cfg.TeachingPoints = _teachingPoints.ToList();

                // 2. Đồng bộ ngược từ Teaching Points sang các thuộc tính vận hành chính của máy Nitto Press
                var standbyPt = _teachingPoints.FirstOrDefault(p => p.StepType == "Standby" || p.Id == 1);
                if (standbyPt != null)
                {
                    cfg.StandbyPosition = standbyPt.Position;
                    if (standbyPt.Speed > 0) cfg.RetractVelocity = standbyPt.Speed;
                }

                var clampPt = _teachingPoints.FirstOrDefault(p => p.TriggerVision || p.StepType == "CheckVision" || p.Id == 2);
                if (clampPt != null)
                {
                    cfg.ClampingPosition = clampPt.Position;
                    if (clampPt.Speed > 0) cfg.ClampingVelocity = clampPt.Speed;
                    if (clampPt.DwellTimeMs > 0) cfg.ForceDwellTimeMs = clampPt.DwellTimeMs;
                }

                if (motion != null)
                {
                    motion.Config = cfg;
                }

                SaveConfigToFile(cfg);
                dgTeachingPoints.Items.Refresh();
                Motion_OnLogMessage($"[Teaching] Saved {_teachingPoints.Count} points successfully (Standby: {cfg.StandbyPosition:F2}mm @ {cfg.RetractVelocity:F1}mm/s, Clamp: {cfg.ClampingPosition:F2}mm @ {cfg.ClampingVelocity:F1}mm/s).");
                MessageBox.Show("Teaching Points saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save Points Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnReloadPoints_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(Common.MotionConfigFile))
                {
                    string json = File.ReadAllText(Common.MotionConfigFile);
                    var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<MotionConfig>(json, Common.JsonPrivate);
                    if (cfg != null)
                    {
                        if (MotionSequenceManager.Instance.Motion != null)
                        {
                            MotionSequenceManager.Instance.Motion.Config = cfg;
                        }
                        LoadTeachingPoints(cfg);
                        Motion_OnLogMessage("[Teaching] Teaching points reloaded from motion_config.json.");
                        return;
                    }
                }

                LoadTeachingPoints();
                Motion_OnLogMessage("[Teaching] Teaching points reloaded.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Reload Points Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion

        #region Simulation & Alarms
        private void ChkTestProgramMode_Changed(object sender, RoutedEventArgs e)
        {
            MotionSequenceManager.Instance.IsTestProgramMode = chkTestProgramMode.IsChecked == true;
            Motion_OnLogMessage($"[Test Program] Mock Simulation Mode: {(MotionSequenceManager.Instance.IsTestProgramMode ? "ENABLED" : "DISABLED")}");
        }

        private void ChkForceVisionOk_Changed(object sender, RoutedEventArgs e)
        {
            var cfg = MotionSequenceManager.Instance.Motion?.Config;
            if (cfg == null) return;

            cfg.ForceVisionOk = chkForceVisionOk.IsChecked == true;
            Motion_OnLogMessage($"[Vision] Force Vision OK override: {(cfg.ForceVisionOk ? "ENABLED" : "DISABLED")}");
        }

        private void CbMockVisionResult_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbMockVisionResult?.SelectedItem is ComboBoxItem item)
            {
                MotionSequenceManager.Instance.MockVisionResult = item.Content?.ToString() ?? "OK";
            }
        }

        private void BtnClearAlarm_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion != null)
            {
                motion.ClearAlarm(_currentAxis);
                txtAlarmCode.Text = "0x0000 (Cleared)";
                txtAlarmCode.Foreground = TileGreenBrush;
                Motion_OnLogMessage($"[Alarm] Alarm cleared on Axis {_currentAxis}");
            }
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            _alarmHistory.Clear();
        }

        private void BtnForceServoOn_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;

            Motion_OnLogMessage($"[Diag] Executing CiA 402 Force Servo ON on Axis {_currentAxis}...");
            bool ok = motion.ForceServoOn(_currentAxis);
            Motion_OnLogMessage($"[Diag] Force Servo ON Axis {_currentAxis} result: {(ok ? "SUCCESS" : "FAILED")}");
        }

        private void BtnToggleEmg_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;

            bool ok = motion.ToggleEmgInversion();
            short currentInv = motion.GetEmgInversion();
            Motion_OnLogMessage($"[Diag] Toggled EMG Trigger Level Inversion: current = {currentInv} (Result: {(ok ? "OK" : "FAILED")})");
        }

        private void BtnScanBus_Click(object sender, RoutedEventArgs e)
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion == null) return;

            Motion_OnLogMessage("[Diag] Initiating EtherCAT Bus Scan & XML reload...");
            bool ok = motion.ScanBus();
            UpdateMasterStatusUI();
            Motion_OnLogMessage($"[Diag] EtherCAT Bus Scan result: {(ok ? "SUCCESS" : "FAILED")}, Master Status = {motion.MasterStatus}");
        }
        #endregion

        #region Advanced Machine Settings
        private void LoadMotionConfigToUI(MotionConfig specificConfig = null)
        {
            try
            {
                var cfg = specificConfig ?? MotionSequenceManager.Instance.Motion?.Config;
                if (cfg == null && File.Exists(Common.MotionConfigFile))
                {
                    string json = File.ReadAllText(Common.MotionConfigFile);
                    cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<MotionConfig>(json);
                }

                if (cfg != null)
                {
                    var axisCfg = cfg.Axes?.FirstOrDefault(a => a.AxisIndex == _currentAxis) ?? new AxisConfig();

                    txtPulsePerUnit.Text = axisCfg.PulsesPerUnit.ToString(CultureInfo.InvariantCulture);
                    txtMaxVel.Text = axisCfg.MaxVelocity.ToString(CultureInfo.InvariantCulture);
                    txtMaxAcc.Text = axisCfg.DefaultProfile.Acceleration.ToString(CultureInfo.InvariantCulture);
                    txtMaxDec.Text = axisCfg.DefaultProfile.Deceleration.ToString(CultureInfo.InvariantCulture);
                    txtClampJogSpeed.Text = cfg.ClampJogVelocity.ToString(CultureInfo.InvariantCulture);
                    chkForceVisionOk.IsChecked = cfg.ForceVisionOk;

                    chkEnableSoftLimits.IsChecked = axisCfg.EnableSoftwareLimits;
                    txtSoftLimitPos.Text = axisCfg.SoftwareLimitPositive.ToString(CultureInfo.InvariantCulture);
                    txtSoftLimitNeg.Text = axisCfg.SoftwareLimitNegative.ToString(CultureInfo.InvariantCulture);

                    cboHomingMode.SelectedIndex = 0;
                    txtHomeHighSpeed.Text = axisCfg.Homing.HighVelocity.ToString(CultureInfo.InvariantCulture);
                    txtHomeLowSpeed.Text = axisCfg.Homing.LowVelocity.ToString(CultureInfo.InvariantCulture);
                    txtHomeOffset.Text = axisCfg.Homing.OffsetPulses.ToString(CultureInfo.InvariantCulture);
                    

                    // Nitto IO bit mapping
                    if (cfg.IO != null)
                    {
                        txtIoBitTriggerLeft.Text = cfg.IO.TriggerBtnLeftDIBit.ToString();
                        txtIoBitTriggerRight.Text = cfg.IO.TriggerBtnRightDIBit.ToString();
                        txtIoBitForceReached.Text = cfg.IO.ForceReachedDIBit.ToString();
                        txtIoBitSensorHomeUp.Text = cfg.IO.SensorHomeUpDIBit.ToString();
                        txtIoBitSensorDownLimit.Text = cfg.IO.SensorDownLimitDIBit.ToString();
                        txtIoBitSensorPartPresent.Text = cfg.IO.SensorPartPresentDIBit.ToString();
                        txtIoBitSafetyStop.Text = cfg.IO.SystemStopDIBit.ToString();
                        txtIoBitSystemStop.Text = cfg.IO.BrakeServoDOBit.ToString();
                        txtIoBitCamTrigger.Text = cfg.IO.Channel1LightTriggerDOBit.ToString();
                        txtIoBitTowerGreen.Text = cfg.IO.Button1LampDOBit.ToString();
                        txtIoBitTowerRed.Text = cfg.IO.Button2LampDOBit.ToString();
                        txtIoBitTowerBuzzer.Text = cfg.IO.LoadCellResetDOBit.ToString();
                        txtIoBitBacklight.Text = cfg.IO.LoadCellHoldDOBit.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Motion_OnLogMessage($"[Config Error] Load failed: {ex.Message}");
            }
        }

        private void BtnSaveMotionConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = MotionSequenceManager.Instance.Motion?.Config ?? new MotionConfig();
                var axisCfg = cfg.Axes?.FirstOrDefault(a => a.AxisIndex == _currentAxis);
                if (axisCfg == null)
                {
                    axisCfg = new AxisConfig { AxisIndex = _currentAxis, AxisName = $"Axis {_currentAxis}" };
                    cfg.Axes.Add(axisCfg);
                }

                double.TryParse(txtPulsePerUnit.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double ppu);
                axisCfg.PulsesPerUnit = ppu > 0 ? ppu : 1000.0;

                double.TryParse(txtMaxVel.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double maxVel);
                axisCfg.MaxVelocity = maxVel > 0 ? maxVel : 50000;

                double.TryParse(txtMaxAcc.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double maxAcc);
                axisCfg.DefaultProfile.Acceleration = maxAcc > 0 ? maxAcc : 500000;

                double.TryParse(txtMaxDec.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double maxDec);
                axisCfg.DefaultProfile.Deceleration = maxDec > 0 ? maxDec : 500000;

                double.TryParse(txtClampJogSpeed.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double clampJogSpeed);
                cfg.ClampJogVelocity = clampJogSpeed > 0 ? clampJogSpeed : 10.0;
                cfg.ForceVisionOk = chkForceVisionOk.IsChecked == true;

                axisCfg.EnableSoftwareLimits = chkEnableSoftLimits.IsChecked == true;
                double.TryParse(txtSoftLimitPos.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double softPos);
                axisCfg.SoftwareLimitPositive = softPos;
                double.TryParse(txtSoftLimitNeg.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double softNeg);
                axisCfg.SoftwareLimitNegative = softNeg;

                axisCfg.Homing.HomeMethod = 28;
                double.TryParse(txtHomeHighSpeed.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double hSpd);
                axisCfg.Homing.HighVelocity = hSpd > 0 ? hSpd : 5000;
                double.TryParse(txtHomeLowSpeed.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double lSpd);
                axisCfg.Homing.LowVelocity = lSpd > 0 ? lSpd : 1000;
                int.TryParse(txtHomeOffset.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out int hOff);
                axisCfg.Homing.OffsetPulses = hOff;

                // Nitto IO bit mapping
                if (cfg.IO == null) cfg.IO = new IOConfig();
                int.TryParse(txtIoBitTriggerLeft.Text, out int trigL); cfg.IO.TriggerBtnLeftDIBit = trigL;
                int.TryParse(txtIoBitTriggerRight.Text, out int trigR); cfg.IO.TriggerBtnRightDIBit = trigR;
                int.TryParse(txtIoBitForceReached.Text, out int forceDi); cfg.IO.ForceReachedDIBit = forceDi;
                int.TryParse(txtIoBitSensorHomeUp.Text, out int homeDi); cfg.IO.SensorHomeUpDIBit = homeDi;
                int.TryParse(txtIoBitSensorDownLimit.Text, out int downDi); cfg.IO.SensorDownLimitDIBit = downDi;
                int.TryParse(txtIoBitSensorPartPresent.Text, out int partDi); cfg.IO.SensorPartPresentDIBit = partDi;
                int.TryParse(txtIoBitSafetyStop.Text, out int systemStopDi); cfg.IO.SystemStopDIBit = systemStopDi;
                int.TryParse(txtIoBitSystemStop.Text, out int brakeDo); cfg.IO.BrakeServoDOBit = brakeDo;
                int.TryParse(txtIoBitCamTrigger.Text, out int lightDo); cfg.IO.Channel1LightTriggerDOBit = lightDo;
                int.TryParse(txtIoBitTowerGreen.Text, out int btn1LampDo); cfg.IO.Button1LampDOBit = btn1LampDo;
                int.TryParse(txtIoBitTowerRed.Text, out int btn2LampDo); cfg.IO.Button2LampDOBit = btn2LampDo;
                int.TryParse(txtIoBitTowerBuzzer.Text, out int resetDo); cfg.IO.LoadCellResetDOBit = resetDo;
                int.TryParse(txtIoBitBacklight.Text, out int holdDo); cfg.IO.LoadCellHoldDOBit = holdDo;

                SaveConfigToFile(cfg);
                MessageBox.Show("Advanced Machine Configuration saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Save Config Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnReloadMotionConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(Common.MotionConfigFile))
                {
                    string json = File.ReadAllText(Common.MotionConfigFile);
                    var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<MotionConfig>(json);
                    if (cfg != null)
                    {
                        LoadMotionConfigToUI(cfg);
                        LoadTeachingPoints(cfg);
                        Motion_OnLogMessage("[Config] Configuration reloaded successfully from file.");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Reload Config Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveConfigToFile(MotionConfig config)
        {
            string dir = Path.GetDirectoryName(Common.MotionConfigFile);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(config, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(Common.MotionConfigFile, json);
            Motion_OnLogMessage("[Config] Saved to motion_config.json");
        }
        #endregion

        #region Activity Logs
        private void Motion_OnLogMessage(string msg)
        {
            Common.Info(msg);
            Dispatcher.InvokeAsync(() =>
            {
                if (txtMotionLogs == null) return;

                string timeStampedMsg = msg.StartsWith("[") ? msg : $"[{DateTime.Now:HH:mm:ss}] {msg}";
                _logLines.Enqueue(timeStampedMsg);

                while (_logLines.Count > MaxLogLines)
                {
                    _logLines.Dequeue();
                }

                txtMotionLogs.Text = string.Join(Environment.NewLine, _logLines);
                txtMotionLogs.ScrollToEnd();
            });
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            if (txtMotionLogs != null) txtMotionLogs.Clear();
        }

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (txtMotionLogs != null) Clipboard.SetText(txtMotionLogs.Text);
            }
            catch
            {
            }
        }

        private void UpdateMasterStatusUI()
        {
            var motion = MotionSequenceManager.Instance.Motion;
            if (motion != null)
            {
                txtMasterStatus.Text = motion.IsMasterOp ? "OP (6)" : $"State {motion.MasterStatus}";
            }
        }
        #endregion

        #region I/O Monitor Logic
        private void InitIoList()
        {
            if (DiItems.Count > 0) return;

            // Khởi tạo 12 cổng Digital Inputs (DI 00..11) theo cấu hình máy Nitto Press
            string[] diNames = new string[12]
            {
                "X01 (Spare)",
                "X02 - Btn_01",
                "X03 - Btn_02",
                "X04 (Spare)",
                "X05 (Spare)",
                "X06 (Spare)",
                "X07 (Spare)",
                "X08 (Spare)",
                "X09 / DI8 (Unused)",
                "X10 - LC_Low (Unused)",
                "X11 - Load Cell OK",
                "X12 - LC_High (Unused)"
            };

            for (short i = 0; i < 12; i++)
            {
                DiItems.Add(new IoPinDisplayItem { Pin = i, Name = diNames[i], IsOutput = false });
            }

            // Khởi tạo 16 cổng Digital Outputs (DO 00..15) theo cấu hình máy Nitto Press
            string[] doNames = new string[16]
            {
                "Y01 - Brake_Servo",
                "Y02 - Btn1_Lamp",
                "Y03 - Btn2_Lamp",
                "Y04 - LC_Reset",
                "Y05 - LC_Hold",
                "Y06 (Spare)",
                "Y07 (Spare)",
                "Y08 (Spare)",
                "Y09 - Ch1_Light_Trigger",
                "Y10 (Spare)",
                "Y11 (Spare)",
                "Y12 (Spare)",
                "Y13 (Spare)",
                "Y14 (Spare)",
                "Y15 (Spare)",
                "Y16 (Spare)"
            };

            for (short i = 0; i < 16; i++)
            {
                DoItems.Add(new IoPinDisplayItem { Pin = i, Name = doNames[i], IsOutput = true });
            }
        }

        private void BtnToggleDO_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is IoPinDisplayItem item)
            {
                bool newState = !item.State;
                var pcieIo = IoJobCtrl.GetIOcardCtrl();
                if (pcieIo != null && pcieIo.IsInit)
                {
                    if (item.Pin >= 0 && item.Pin < pcieIo.OutputChannels)
                    {
                        pcieIo.SetChannelOutput(item.Pin, newState);
                        item.State = newState;
                    }
                    return;
                }

                // Fallback sang motion
                var motion = MotionSequenceManager.Instance.Motion;
                if (motion != null)
                {
                    motion.SetDigitalOutput(item.Pin, newState);
                    item.State = newState;
                }
            }
        }

        private void BtnResetAllOutputs_Click(object sender, RoutedEventArgs e)
        {
            var pcieIo = IoJobCtrl.GetIOcardCtrl();
            if (pcieIo != null && pcieIo.IsInit)
            {
                for (int ch = 0; ch < pcieIo.OutputChannels; ch++)
                {
                    pcieIo.SetChannelOutput(ch, false);
                }
            }

            var motion = MotionSequenceManager.Instance.Motion;
            if (motion != null)
            {
                for (short i = 0; i < 16; i++)
                {
                    motion.SetDigitalOutput(i, false);
                }
            }

            foreach (var item in DoItems)
            {
                item.State = false;
            }

            Motion_OnLogMessage("[Manual] Reset all Digital Outputs to LOW (0).");
        }

        #endregion

        /// <summary>
        /// Lấy tọa độ hiện tại của trục servo gán trực tiếp vào dòng được bấm trong bảng Teaching Points
        /// </summary>
        private void BtnGetPosRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TeachingPoint pt)
            {
                var sts = MotionSequenceManager.Instance.Motion?.GetAxisState(_currentAxis);
                if (sts != null)
                {
                    pt.Position = Math.Round(sts.ActualPosition, 3);
                    pt.AxisIndex = _currentAxis;
                    dgTeachingPoints.Items.Refresh();
                    Motion_OnLogMessage($"[Teaching] Updated position for point '{pt.Name}': {pt.Position:F3} mm");
                }
            }
        }
    }

    public class IoPinDisplayItem : System.ComponentModel.INotifyPropertyChanged
    {
        private static readonly SolidColorBrush DiActiveBgBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x3E, 0x2B));
        private static readonly SolidColorBrush DiActiveBorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
        private static readonly SolidColorBrush DoActiveBgBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x27, 0x12));
        private static readonly SolidColorBrush DoActiveBorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
        private static readonly SolidColorBrush ItemOffBgBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
        private static readonly SolidColorBrush ItemOffBorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

        private static readonly SolidColorBrush DiLedOnBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
        private static readonly SolidColorBrush DoLedOnBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
        private static readonly SolidColorBrush LedOffBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

        public short Pin { get; set; }
        public string PinLabel => (IsOutput ? "DO " : "DI ") + Pin.ToString("D2");
        public string Name { get; set; }
        public bool IsOutput { get; set; }

        private bool _state;
        public bool State
        {
            get => _state;
            set
            {
                if (_state != value)
                {
                    _state = value;
                    OnPropertyChanged(nameof(State));
                    OnPropertyChanged(nameof(StateBrush));
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(CardBgBrush));
                    OnPropertyChanged(nameof(CardBorderBrush));
                }
            }
        }

        public Brush StateBrush => State
            ? (IsOutput ? DoLedOnBrush : DiLedOnBrush)
            : LedOffBrush;

        public Brush CardBgBrush => State
            ? (IsOutput ? DoActiveBgBrush : DiActiveBgBrush)
            : ItemOffBgBrush;

        public Brush CardBorderBrush => State
            ? (IsOutput ? DoActiveBorderBrush : DiActiveBorderBrush)
            : ItemOffBorderBrush;

        public string StateText => State ? "HIGH (1)" : "LOW (0)";

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));
    }

}
