using BeeLightModule;
using BeeMotionModule.Models;
using BeevisionSolution.Controller;
using BeevisionSolution.Jobs;
using BeevisionSolution.LocalDB;
using BeevisionSolution.Models;
using BeevisionSolution.Utils;
using Cognex.VisionPro;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using Microsoft.Extensions.FileSystemGlobbing;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using static BeevisionSolution.Utils.Common;
using TDistance = System.Tuple<double, double, double, double>;
using TPose = System.Tuple<double, double, double, bool, double>;
using static BeevisionSolution.Views.ImageView;

namespace BeevisionSolution.Views
{
    /// <summary>
    /// Interaction logic for MainWindow2.xaml
    /// </summary>
    public partial class MainWindow2 : MahApps.Metro.Controls.MetroWindow
    {
        public OnJobLoadedDone OnAllJobLoadedDone;
        public OnJobProcessing OnJobToolProcessing;
        public OnHandEye OnHandEyeEvent;
        public OnHandEyeBegin OnHandEyeBeginEvent;
        public OnHandEyeEnd OnHandEyeEndEvent;
        public OnJobCompleted OnFunctionJobComplete;
        public OnImageHandle OnImageHandleEvent;
        public OnProductRetrieveHandle OnProductRetrieveHandleEvent;
        public OnJobLoadedDone OnPlcLoadedDone;
        public bool firstInitFlag = true;
        public bool firstLoadedFlag = true;
        public bool lockedFlag = true;
        public string previousSelection = string.Empty;
        public OnSettingLoadedDone OnSettingLoadedDone;
        public SerialPortController serialPort;
        public DatamanController dtmController;
        public event EventHandler EditRequested;
        public Func<string, Task> OnChangeModelPlcTrigger;

        private Timer cleanupTimer;
        private bool _cleanupTimerDisposed;
        private bool _cleanerLogOnce;
        private bool IsRunningCleanTask = false;
        private bool closeMe = false;
        private int CurrentCamera = -1;
        public int ServerConnections => 0;
        private PlcCam plcCam;
        private bool _skipCloseConfirmation = false;
        private IpcIOControl ioCard;
        private bool _isMotionLogSubscribed = false;
        public MainWindow2()
        {
            InitializeComponent();
            DataContext = this;
            JobController.SetUIDispatcher(Application.Current.Dispatcher);
            this.MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight - 10;
        }


        internal void SetLanguageDictionary(Lang lang)
        {
            var merged = Application.Current.Resources.MergedDictionaries;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                try
                {
                    var uri = merged[i]?.Source;
                    if (uri == null)
                        continue;
                    string s = uri.IsAbsoluteUri ? uri.LocalPath : uri.OriginalString;
                    if (string.IsNullOrEmpty(s))
                        continue;
                    if (AppearsBeeLanguagePack(s))
                        merged.RemoveAt(i);
                }
                catch { /* skip entry */ }
            }

            ResourceDictionary dict = new ResourceDictionary();
            switch (lang)
            {
                case Lang.en:
                    dict.Source = new Uri("..\\Resources\\en.xaml", UriKind.Relative);
                    break;
                case Lang.vn:
                    dict.Source = new Uri("..\\Resources\\vn.xaml", UriKind.Relative);
                    break;
                default:
                    dict.Source = new Uri("..\\Resources\\en.xaml", UriKind.Relative);
                    break;
            }

            merged.Add(dict);
        }

        private static bool AppearsBeeLanguagePack(string mergedSourceFragment)
        {
            if (string.IsNullOrEmpty(mergedSourceFragment))
                return false;
            bool res = mergedSourceFragment.IndexOf("resources", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!res)
                return false;
            bool en = mergedSourceFragment.IndexOf("en.xaml", StringComparison.OrdinalIgnoreCase) >= 0;
            bool vn = mergedSourceFragment.IndexOf("vn.xaml", StringComparison.OrdinalIgnoreCase) >= 0;
            return en || vn;
        }
        

        private async void MetroWindow_ContentRendered(object sender, EventArgs e)
        {
            SetLanguageDictionary((Lang)Settings.CurrentLanguage);
            bool loginSuccess = await ShowLoginDialog();

            if (!loginSuccess)
            {
                _skipCloseConfirmation = true;
                Application.Current.Shutdown();
                return;
            }
            //Init dataman controller
            //dtmController = new DatamanController();

            await LoadJobs(true);
            await Dispatcher.InvokeAsync(() => RaiseEditRequested(),
                                     System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            // Opt-in via app.json: EnableWatcherAndIOInit (default false).
            if (Settings.EnableWatcherAndIOInit)
                await WatcherInit();
            else
                Info("[Watcher/IO] WatcherInit skipped - EnableWatcherAndIOInit is false in app.json");
            
            if (Settings.EnableWatcherAndIOInit)
                InitIOController();
            else
                Info("[Watcher/IO] InitIOController skipped - EnableWatcherAndIOInit is false in app.json");
            await PlcInit();
            lockedFlag = false;
            Topmost = false;

            InitCleaner(null);
        }

        private void RaiseEditRequested()
        {
            EditRequested?.Invoke(null, EventArgs.Empty);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InitCleaner(null);
            Common.InitAutoZipBackup();
            OnSettingLoadedDone?.Invoke(this);
        }


        private async Task<bool> ShowLoginDialog()
        {
            ImageView.CurrentInstance?.CollapsedAllLiveDisplay();
            BeeSql.InitializeDatabase();
            BeeSql.Open();
            AccountManager.InitializeAccountsTable();
            AccountManager.InitializeDefaultAccounts();

            var operatorAccount = AccountManager.GetAllAccounts()
                .FirstOrDefault(a => a.Role == UserRole.Operator && a.IsActive);
            if (operatorAccount == null)
            {
                var newOperator = new Account
                {
                    Username = "operator",
                    Password = "operator",
                    FullName = "Operator",
                    Role = UserRole.Operator,
                    IsActive = true
                };

                bool created = AccountManager.CreateAccount(newOperator);
                if (created)
                {
                    Common.Info("Create default engineer account.");
                    operatorAccount = newOperator;
                }
                else
                {
                    Common.Bug("Cannot create account engineer");
                    operatorAccount = AccountManager.GetAllAccounts()
                        .FirstOrDefault(a => a.Role == UserRole.Engineer && a.IsActive);
                }
            }
            if (operatorAccount != null)
            {
                Common.CurrentUser = operatorAccount;

                Common.CurrentOperationMode = OperationMode.Operator;
                ImageView.CurrentInstance?.ShowToolDisplay(true); 

                Common.Info($"Create successfully: {operatorAccount.Username} (Role: Operator)");

                ImageView.CurrentInstance?.VisibleAllLiveDisplay();
                return true;
            }


            var loginDialog = new ViewComponents.LoginDialog(this);

            await this.ShowMetroDialogAsync(loginDialog);
            await loginDialog.WaitUntilUnloadedAsync();

            if (loginDialog.LoggedInAccount != null)
            {
                Common.Info($"User {loginDialog.LoggedInAccount.Username} logged in successfully");
                ImageView.CurrentInstance?.VisibleAllLiveDisplay();
                return true;
            }
            else
            {
                Common.Info("Login cancelled by user");
                return false;
            }
        }

        private void DisposeCleanupTimer()
        {
            if (_cleanupTimerDisposed)
                return;
            _cleanupTimerDisposed = true;
            try
            {
                cleanupTimer?.Dispose();
                cleanupTimer = null;
            }
            catch { /* ignore */ }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_skipCloseConfirmation)
            {
                BeeSql.Close();
                DisposeCleanupTimer();
                Cleanup();
                return;
            }
            MessageBoxResult result = MessageBox.Show(
                (string)TryFindResource("msgConfirmExit"),
                (string)TryFindResource("strExit"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
            }
            else
            {
                BeeSql.Close();
                DisposeCleanupTimer();
                Cleanup();
                //CloseAllCamera();

                OnExit();
            }
        }

        public void OnExit()
        {
            Application.Current.Shutdown();
        }

        private int jobsLoaded = 0;
        private bool bLoaded = false;

        private async Task LoadJobs(bool force = false)
        {
            var mySettings = new MetroDialogSettings()
            {
                DialogTitleFontSize = DialogTitleFontSize,
                DialogMessageFontSize = DialogMessageFontSize,
                AnimateShow = true,
                AnimateHide = false
            };
            imageView?.CollapsedAllLiveDisplay();
            var controller = await this.ShowProgressAsync($"{Settings.AppName} " + (string)TryFindResource("msgLoadingJob"), "Progress message", false, mySettings);
            controller.SetIndeterminate();
            jobsLoaded = 0;

            var sw = new Stopwatch();
            sw.Start();

            var lst = JobController.GetAllJobs(false);
            //var lst = Common.GetAllJobs();
            bLoaded = false;

            if ((null == lst) || (lst.Count < 1))
                goto finish;

            foreach (var job in lst)
            {
                controller.SetMessage(String.Format((string)TryFindResource("msgLoading") + " {0} {1}/{2}", job.Name, ++jobsLoaded, lst.Count));
                job.OnResult += RaiseAlignOnResultEvent;
                await Task.Run(() => job.Init());
            }
            //VidiCommon.LoadWorkspace();
            sw.Stop();
            Info("Loading time: {0} miliseconds", sw.ElapsedMilliseconds);

            OnAllJobLoadedDone?.Invoke(this);
            OnSettingLoadedDone?.Invoke(this);
            StartAllCamerasLogger();
            JobController.LightingInit();

            bLoaded = true;
        finish:
            if (!bLoaded)
            {
                OnAllJobLoadedDone?.Invoke(this);
                await controller.CloseAsync();
                bLoaded = true;                
            }
            else
            {
                await controller.CloseAsync();
            }
        imageView?.VisibleAllLiveDisplay();

        }

        private async Task WatcherInit()
        {
            await Task.Delay(1);
            var jobs = JobController.GetAllJobs(false);
            foreach(var job in jobs)
            {
                if(job is WatcherJob wJob)
                {
                    // invoke to get controller
                    wJob.StartMonitorFolder();
                    //wJob.OnTrigger += JobController.DoWatcherJob;
                }
            }
        }

        public async void RaiseOnReqModelChange(string modelName)
        {
            try
            {
                Info("RaiseOnReqModelChange called - modelName: '{0}'", modelName ?? "null");
                if (string.IsNullOrEmpty(modelName))
                {
                    Bug("RaiseOnReqModelChange: modelName is null or empty, cannot load model");
                    return;
                }

                if (OnChangeModelPlcTrigger != null)
                {
                    await OnChangeModelPlcTrigger.Invoke(modelName);
                }
                else
                {
                    await LoadModelDirectly(modelName);
                }
            }
            catch (Exception ex)
            {
                Bug("RaiseOnReqModelChange Exception: {0}\nStackTrace: {1}", ex.Message, ex.StackTrace);
            }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                {
                    return result;
                }
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                {
                    return childOfChild;
                }
            }
            return null;
        }

        private async Task LoadModelDirectly(string modelName)
        {
            try
            {
                Info("LoadModelDirectly: Checking if model '{0}' exists", modelName);

                // Kiểm tra model có tồn tại không
                var allProfiles = Common.GetAllProfiles();
                if (!allProfiles.Contains(modelName))
                {
                    Bug("LoadModelDirectly: Model '{0}' not found in profiles list", modelName);
                    return;
                }

                // Kiểm tra xem model đã được load chưa
                if (modelName.Equals(Common.Settings.CurrentProfile))
                {
                    Info("LoadModelDirectly: Model '{0}' is already loaded", modelName);
                    return;
                }

                Info("LoadModelDirectly: Loading model '{0}'", modelName);

                // Set current profile
                Common.Settings.CurrentProfile = modelName;
                Common.Settings.Save();

                Info("LoadModelDirectly: Profile changed to '{0}', reloading...", modelName);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Reload();
                });

                Info("LoadModelDirectly: Model '{0}' loaded successfully", modelName);
            }
            catch (Exception ex)
            {
                Bug("LoadModelDirectly Exception: {0}\nStackTrace: {1}", ex.Message, ex.StackTrace);
            }
        }

       
        private async Task PlcInit()
        {
            await Task.Delay(1);
            var motionCfg = Common.GetObjectFromFile<MotionConfig>(Common.MotionConfigFile) ?? new MotionConfig();
            if (motionCfg != null && motionCfg.EnableMotionControl)
            {
                motionCfg.ConfigDirectory = Common.ConfigFolder;

                // 1. Đăng ký nhận toàn bộ log Motion vào log tổng thể
                if (!_isMotionLogSubscribed)
                {
                    MotionSequenceManager.Instance.OnLog += (msg) => Info(msg);
                    _isMotionLogSubscribed = true;
                }

                Info("[Motion] Initializing Inovance EtherCAT Motion Controller...");
                bool ok = MotionSequenceManager.Instance.Initialize(motionCfg);

                // 2. Khi Card & Bus đã khởi tạo xong -> Tự động Clear Alarm và Bật Servo ON
                if (ok)
                {
                    var motion = MotionSequenceManager.Instance.Motion;
                    int totalAxes = motionCfg.TotalAxes > 0 ? motionCfg.TotalAxes : (motionCfg.Axes?.Count ?? 1);

                    for (short i = 0; i < totalAxes; i++)
                    {
                        // Gọi chuỗi: Mở phanh PCIe DO 2 -> Clear Emergency -> Servo ON
                        bool svOk = await MotionSequenceManager.Instance.EnableServoSequenceAsync(i);
                        Info("[Motion] Axis {0}: Reset Error & Servo ON -> {1}", i, svOk ? "Done" : "Failed");
                    }
                    
                    // Tự động về gốc (Physical Homing to Home Sensor)
                    for (short i = 0; i < totalAxes; i++)
                    {
                        Info($"[Motion] Auto Homing Axis {i}...");
                        bool homeOk = await motion.HomeAsync(i);
                        if (homeOk)
                        {
                            Info($"[Motion] Axis {i}: Auto Home Done.");
                        }
                        else
                        {
                            Bug($"[Motion Alarm]: Axis {i} Failed - Home error or Timeout");
                        }    
                    }    
                    Info("[Motion] Manual Move Ready.");
                }
                else
                {
                    Bug("[Motion Error] Failed to Init Controller. Please check Ethercat Cable and Card.");
                }
            }
        }

        private void CheckAutoRunning()
        {
        }
        private void InitIOController()
        {
            Bug("Init IO cards...");
            IoJobCtrl.LoadIoConfig();
            ioCard = IoJobCtrl.GetIOcardCtrl();
            if (ioCard != null)
            {
                if (!ioCard.IsInit)
                {
                    ioCard.InitGPIO();
                }
                ioCard.OnPinTriggered += OnIOTriggered;
                var ioThread = new Thread(ioCard.DoSync);
                ioThread.IsBackground = true;
                Bug("Before start");
                ioThread.Start();
                Bug("IO controller inits successfully!");
            }
            else
            {
                Bug("IO card controller is null");
            }
            IoJobCtrl.LoadIoJobs();
        }

        private async void OnIOTriggered(int pinNo, bool isOn, IpcIOControl ioCard)
        {
            if (!isOn)
            {
                return;
            }

            Bug("IO {0} got triggered", pinNo);

            var ioJob = IoJobCtrl.GetJobByPin(pinNo);
            if (ioJob == null)
            {
                Bug("No IO job found for pin {0}", pinNo);
                return;
            }

            try
            {
                IspJob ispJob = null;

                ispJob = JobController.GetIspJob(ioJob.JobName.ToString());

                if (ispJob == null)
                {
                    Bug("Cannot find IspJob for IO job pin {0}, JobName: {1}", pinNo, ioJob.JobName);
                    // Set NG output if no find jobs
                    if (ioJob.PinOutNG > 0)
                    {
                        ioCard.SetPinOutput(ioJob.PinOutNG, true);
                        Thread.Sleep(ioJob.DelayNGTime);
                        ioCard.SetPinOutput(ioJob.PinOutNG, false);
                    }
                    return;
                }

                // run inspection with IO 
                bool result = await JobController.RunInspectionByIO(ispJob, ioJob);

                // Set result output 
                if (result)
                {
                    if (ioJob.PinOutOK > 0)
                    {
                        ioCard.SetPinOutput(ioJob.PinOutOK, true);
                        Thread.Sleep(ioJob.DelayTime);
                        ioCard.SetPinOutput(ioJob.PinOutOK, false);
                    }
                }
                else
                {
                    if (ioJob.PinOutNG > 0)
                    {
                        ioCard.SetPinOutput(ioJob.PinOutNG, true);
                        Thread.Sleep(ioJob.DelayNGTime);
                        ioCard.SetPinOutput(ioJob.PinOutNG, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Bug("OnIOTriggered Exception: {0}", ex.Message);
                Bug(ex.StackTrace);
                // Set NG output khi có exception
                if (ioJob != null && ioJob.PinOutNG > 0)
                {
                    ioCard.SetPinOutput(ioJob.PinOutNG, true);
                    Thread.Sleep(ioJob.DelayNGTime);
                    ioCard.SetPinOutput(ioJob.PinOutNG, false);
                }
            }
        }
        bool SetEquipmentId(int NumberParam, string StringParam)
        {
            Settings.EquipmentID = StringParam;
            return true;
        }
    
        #region events and common jobs
        private void InitCleaner(Task task)
        {
            try
            {
                cleanupTimer?.Dispose();
            }
            catch { /* ignore */ }

            cleanupTimer = new Timer(DoCleanup, null, 0, 120000);
            if (!_cleanerLogOnce)
            {
                Info("Cleaning task was created");
                _cleanerLogOnce = true;
            }
        }

        private void DoCleanup(Object obj)
        {
            if ((!bLoaded) || (IsRunningCleanTask)) return;

            new Thread(() =>
            {
                IsRunningCleanTask = true;
                try
                {
                    var drv = System.IO.Path.GetPathRoot(Settings.LoggingDirectory);
                    var logDrive = new DriveInfo(drv);

                    if ((Settings.SaveImage && Settings.AutoCleanUp) && (null != logDrive))
                    {
                        var usedSpace = (1 - ((double)logDrive.AvailableFreeSpace / logDrive.TotalSize)) * 100;

                        if (usedSpace > Settings.DriveSpaceThreshold)
                        {
                            var dirPath = Settings.LoggingDirectory;
                            var dirs = Directory.GetDirectories(dirPath);
                            var lst = new List<Tuple<DateTime, string>>();

                            if ((null == dirs) || (dirs.Length == 0)) return;

                            var today = DateTime.Now;
                            var cutoffDate = today.AddDays(-Settings.DaysInHistory);
                            foreach (string dir in dirs)
                            {
                                var strDirName = System.IO.Path.GetFileName(dir);
                                var isFormal = Regex.Match(strDirName, @"^\d{4}-\d{2}-\d{2}$").Success;

                                if (isFormal)
                                {
                                    var folderDate = today;

                                    if (DateTime.TryParse(strDirName, out folderDate))
                                    {
                                        if (folderDate < cutoffDate)
                                        {
                                            try
                                            {
                                                Directory.Delete(dir, true);
                                                Info("Deleting old folder (older than {0} days): {1}", Settings.DaysInHistory, dir);
                                            }
                                            catch (Exception ex)
                                            {
                                                Bug("Error deleting folder {0}: {1}", dir, ex.Message);
                                            }
                                        }
                                        else
                                        {
                                            lst.Add(new Tuple<DateTime, string>(folderDate, dir));
                                        }
                                    }
                                }
                            }

                            // Nếu vẫn còn vượt ngưỡng dung lượng sau khi xóa folder cũ, xóa folder cũ nhất
                            if (usedSpace > Settings.DriveSpaceThreshold && lst.Count > 1)
                            {
                                lst.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                                var toDelete = lst[0].Item2;
                                try
                                {
                                    Directory.Delete(toDelete, true);
                                    Info("Deleting oldest folder (space threshold exceeded): {0}", toDelete);
                                }
                                catch (Exception ex)
                                {
                                    Bug("Error deleting folder {0}: {1}", toDelete, ex.Message);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { Bug("Exception: {0}\nStackTrace: {1}", ex.Message, ex.StackTrace); }

                IsRunningCleanTask = false;
            })
            { IsBackground = true }
            .Start();
        }

        void OnServerHandEyeBegin(string[] Params)
        {
            OnHandEyeBeginEvent?.Invoke(Params);
        }

        void OnServerHandEye(string[] Params)
        {
            //IsHandEye = true;
            OnHandEyeEvent?.Invoke(Params);
        }

        void OnServerHandEyeEnd(string[] Params)
        {
            OnHandEyeEndEvent?.Invoke(Params);
        }

        void OnServerReceivedMessage(string msg)
        {
            //AddClientEntry(msg);
        }

        void OnServerResponeMessage(string msg)
        {
            //AddServerEntry(msg);
        }
        void RaiseJobProcessing(string jobName, string msg)
        {
            //var job = GetFunctionJob(jobName);
            //if (job != null)
            //{
            //    OnJobToolProcessing?.Invoke(job, msg);
            //}
        }

        void RaiseOnResultEvent(string[] Params, uint CamId, uint DisplayId, string strCamName, string strPrefix, ICogImage img, List<Object> lstResult, bool bOverall = true, double actualDist = double.NaN)
        {
            var strFile = GetImageFileName(strPrefix.Replace(',', '_'));
            OnFunctionJobComplete?.Invoke(lstResult, (int)DisplayId, img, strCamName, strFile, false, false, actualDist);

            //OnImageHandleEvent?.Invoke(lstResult, (int)DisplayId, img, strCamName);
            OnProductRetrieveHandleEvent?.Invoke(lstResult, (int)DisplayId, strCamName, bOverall, actualDist);
        }

        void RaiseAlignOnResultEvent(int displayId, string strCamName, string strPrefix, ICogImage img, List<Object> lstResult, bool isOK, double actualDist = double.NaN)
        {
            OnImageHandleEvent?.Invoke(lstResult, displayId, img, strCamName, actualDist);
            OnProductRetrieveHandleEvent?.Invoke(lstResult, displayId, strCamName, isOK, actualDist);
        }

        public async void Reload()
        {
            Info("Begin cleanup");
            Cleanup();
            //dtmController.Dispose();

            Info("Done grid, Begin Load jobs");
            await LoadJobs(true);
            if (Settings.EnableWatcherAndIOInit)
                await WatcherInit();
            await PlcInit();

            // Reconfigure logger with new model path (LoggingDirectory = SavingImageDirectory/CurrentProfile)
            StartAllLogger();

            // Refresh TopPanel PLC reference - it was pointing to aborted instances
            RefreshTopPanelPlcReference();
        }

        private void RefreshTopPanelPlcReference()
        {
            try
            {
                var topPanel = FindVisualChild<TopPanel>(this);
                topPanel?.RefreshPlcReference();
            }
            catch (Exception ex)
            {
                Bug("RefreshTopPanelPlcReference Exception: {0}", ex.Message);
            }
        }


        private void Cleanup()
        {
            if (ioCard != null)
            {
                ioCard.Abort();
                ioCard.Dispose();
                ioCard = null;
            }
            try
            {
                MotionSequenceManager.Instance.Motion?.Close();
            }
            catch { }
            JobController.Cleanup();
        }

        
        #endregion

        private void MetroWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F7)
            {
                //DoPlcCamJob(PlcCamManager.GetAllPlcCam()[0], null);
            }
        }

        private void MetroWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!lockedFlag)
            {
                if (e.Key == Key.F10 || e.SystemKey == Key.F10)
                {
                    e.Handled = true;
                    this.Close();
                }
            }
        }
    }
}
