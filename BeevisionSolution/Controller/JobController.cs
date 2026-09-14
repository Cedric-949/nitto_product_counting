using BeevisionSolution.Models;
using BeevisionSolution.Utils;
using BeevisionSolution.ViewModels;
using Cognex.VisionPro;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TPose = System.Tuple<double, double, double, bool, double>;
using static BeevisionSolution.Utils.Common;
using BeeLib.Math;
using BeevisionSolution.Views;
using BeeLightModule;
using System.Web.UI.WebControls;
using BeevisionSolution.Jobs;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Windows;
using static System.Windows.Forms.AxHost;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using DatabaseInterface_MMCV;
using System.IO;
using System.Xml;
using Cognex.VisionPro.ImageFile;
using Cognex.VisionPro.ToolBlock;
using System.Diagnostics;

namespace BeevisionSolution.Controller
{
    public class JobController
    {
        const string DATE_FORMAT = "yyyy-MM-dd";
        const string TIME_FORMAT = "HHmmss";

        private static string lotId = string.Empty;
        private static List<BaseJob> listJobs = new List<BaseJob>();
        private static List<PlcCam> listPlcCam = new List<PlcCam>();
        private static List<LightEthernetControlBase> listLightControllers = new List<LightEthernetControlBase>();
        private static List<LightJob> listLightJobs = new List<LightJob>();

        public static List<PlcCam> GetAllPlcCam() => listPlcCam;
        public static PlcCam GetPlcCamByID(int plcCamId) => listPlcCam.FirstOrDefault(p => p.PlcJobId == plcCamId) ?? new PlcCam { PlcJobId = plcCamId };
        public static List<object> GetAllPlcConfigs() => new List<object>();
        private static List<PcsRotation> listPcsRotation = new List<PcsRotation>();
        private static ConcurrentDictionary<string, string> datamanImages = new ConcurrentDictionary<string, string>();
        private static List<VisionBarcode> listBarcode = new List<VisionBarcode>();
        private static List<ShiftBarcode.BarcodeCheck> listShiftBarcodeCheck = new List<ShiftBarcode.BarcodeCheck>();
        private static List<ShiftBarcode.Barcode> listShiftBarcodeUpload = new List<ShiftBarcode.Barcode>();
        public static event EventHandler<List<CameraJob>> LiveJobsUpdated;
        public static event Action<string> OnSheetIdReceive;
        public static int countResultNG = 0;
        private static readonly object _lock = new object();
        private static bool _isProcessingOpCall = false;
        private static Dispatcher _uiDispatcher;
        private static CogRecordDisplay _cachedCogDisplay;
        private static CogImageFileTool _imageFileTool;
        private static readonly object _cogDisplayLock = new object();
        private static Form _cogDisplayForm;
        private static string _previousSheetId = string.Empty;
        private static readonly object _sheetIdLock = new object();
        internal static AppSettings Settings = GetObjectFromFile<AppSettings>(AppConfigFile);


        public static event Action<int, string> UpdateData;
        public static event Action<int, int, int> UpdateRetryInfo;
        public static event Action<int, double> UpdateAlignTime;
        public static event Action<IEnumerable<int>, double> UpdateCycleTime;
        /// <summary>Raised when a job is about to run, so UI can clear previous result for the given display ID(s).</summary>
        public static event Action<IEnumerable<int>> OnClearDisplayForJob;
        /// <summary>Shows the first raw inspection image without publishing an inspection result.</summary>
        public static event Action<int, ICogImage, string> InspectionPreviewUpdated;

        #region Common Functions
        public static void SetUIDispatcher(Dispatcher dispatcher)
        {
            _uiDispatcher = dispatcher;
        }

        public static void InitializeCachedCogDisplay()
        {
            if (_cachedCogDisplay == null || _cachedCogDisplay.IsDisposed)
            {
                lock (_cogDisplayLock)
                {
                    if (_cachedCogDisplay == null || _cachedCogDisplay.IsDisposed)
                    {
                        // Create a hidden form to host the control
                        _cogDisplayForm = new Form()
                        {
                            WindowState = FormWindowState.Minimized,
                            ShowInTaskbar = false,
                            Size = new System.Drawing.Size(100, 100),
                            Visible = false
                        };

                        _cachedCogDisplay = new CogRecordDisplay();
                        _cogDisplayForm.Controls.Add(_cachedCogDisplay);

                        // Show and hide to ensure proper initialization
                        _cogDisplayForm.Show();
                        _cogDisplayForm.Hide();
                    }
                }
            }
        }
        public static void ReloadSettings()
        {
            try
            {
                Settings = GetObjectFromFile<AppSettings>(AppConfigFile);
                Info("JobController: Settings reloaded successfully");
            }
            catch (Exception ex)
            {
                Bug($"Error reloading Settings in JobController: {ex.Message}");
            }
        }

        // Cleanup method - call this when application closes
        private static void CleanupCachedCogDisplay()
        {
            try
            {
                _cachedCogDisplay?.Dispose();
                _cogDisplayForm?.Dispose();
                _cachedCogDisplay = null;
                _cogDisplayForm = null;
            }
            catch (Exception ex)
            {
                Bug($"Error cleaning up CogRecordDisplay: {ex.Message}");
            }
        }

        public static void Cleanup()
        {
            if (listJobs != null)
            {
                foreach (var job in listJobs)
                {
                    try
                    {
                        job.Dispose();
                        if (job is WatcherJob wjob)
                        {
                            wjob.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Bug($"Error disposing job {job?.Name}: {ex.Message}");
                    }
                }
                listJobs.Clear();
            }
            if (listLightControllers != null)
            {
                foreach (var controller in listLightControllers)
                {
                    try
                    {
                        if (controller != null)
                        {
                            controller.Dispose();
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        Info($"Light controller already disposed, skipping...");
                    }
                    catch (DllNotFoundException dllEx)
                    {
                        Info($"DLL not found during cleanup (expected on shutdown): {dllEx.Message}");
                    }
                    catch (Exception ex)
                    {
                        Bug($"Error disposing light controller: {ex.Message}");
                    }
                }

                listLightControllers.Clear();
            }
            if (listLightJobs != null)
            {
                listLightJobs.Clear();
            }
            if (listPcsRotation != null)
            {
                listPcsRotation.Clear();
            }
            CleanupCachedCogDisplay();
        }

        public static List<BaseJob> GetAllJobs(bool forceReload)
        {
            try
            {
                if (listJobs == null || listJobs.Count < 1)
                {
                    Info("Loading all jobs...");
                    listJobs = GetObjectFromFile<List<BaseJob>>(Common.JobsConfigFile, Common.JsonAbstractJob);
                }

                return listJobs;
            }
            catch (Exception ex)
            {
                Bug("Load jobs failed");
                Bug(ex.StackTrace);
                return null;
            }
        }

        public static List<PcsRotation> GetAllPcsRotationConfig()
        {
            try
            {
                if (listPcsRotation == null || listPcsRotation.Count < 1)
                {
                    Info("Loading all pcs rotation config...");
                    listPcsRotation = GetObjectFromFile<List<PcsRotation>>(Common.PcsRotationConfigFile, JsonPrivate);
                }

                return listPcsRotation;
            }
            catch (Exception ex)
            {
                Bug("Load pcs rotation config failed");
                Bug(ex.StackTrace);
                return null;
            }
        }

        public static IspJob GetIspJobByDisplayId(int displayId)
        {
            try
            {
                if (listJobs.Count > 0 && listJobs != null)
                {
                    foreach (var jobs in listJobs)
                    {
                        if (jobs is IspJob iJob)
                        {
                            if (iJob.DisplayId == displayId)
                            {
                                return iJob;
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Bug("Load camera job by display Id failed");
                Bug(ex.StackTrace);
                return null;
            }
        }


        public static CameraJob GetCameraJob(int cameraId)
        {
            try
            {
                if (listJobs.Count > 0 && listJobs != null)
                {
                    foreach (var jobs in listJobs)
                    {
                        if (jobs is CameraJob cJob)
                        {
                            if (cJob.CamSettings.CameraId == cameraId)
                            {
                                return cJob;
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Bug("Load camera job by Id failed");
                Bug(ex.StackTrace);
                return null;
            }
        }

        public static WatcherJob GetWatcherJob(int plcJobId)
        {
            try
            {
                if (listJobs.Count > 0 && listJobs != null)
                {
                    foreach (var jobs in listJobs)
                    {
                        if (jobs is WatcherJob wJob)
                        {
                            if (wJob.PlcJobId == plcJobId)
                            {
                                return wJob;
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Bug("Load watcher job by plcId failed");
                Bug(ex.StackTrace);
                return null;
            }
        }

        public static IspJob GetIspJob(int plcJobId)
        {
            try
            {
                if (listJobs.Count > 0 && listJobs != null)
                {
                    foreach (var jobs in listJobs)
                    {
                        if (jobs is IspJob iJob)
                        {
                            if (iJob.PlcJobId == plcJobId)
                            {
                                return iJob;
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Bug("Load isp job by plcId failed");
                Bug(ex.StackTrace);
                return null;
            }
        }

        public static AlignJob GetAlignJob(int plcJobId)
        {
            try
            {
                if (listJobs.Count > 0 && listJobs != null)
                {
                    foreach (var jobs in listJobs)
                    {
                        if (jobs is AlignJob aJob)
                        {
                            if (aJob.PlcJobId == plcJobId)
                            {
                                return aJob;
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Bug("Load align job by plcId failed");
                Bug(ex.StackTrace);
                return null;
            }
        }


        public static List<LightEthernetControlBase> GetAllLights()
        {
            try
            {
                if (listLightControllers == null || listLightControllers.Count < 1)
                {
                    Info("Loading all light's controllers...");
                    listLightControllers = GetObjectFromFile<List<LightEthernetControlBase>>(Common.LightsConfigFile, Common.JsonAbstractLight);
                }

                foreach (var lightController in listLightControllers)
                {
                    lightController.Connect();
                    var connect = lightController.IsConnected();
                }

                return listLightControllers;
            }
            catch (Exception ex)
            {
                Bug("Load lights controller failed");
                Bug(ex.StackTrace);
                return null;
            }
        }

        public static List<LightJob> GetAllLightJobs()
        {
            try
            {
                if (listLightJobs == null || listLightJobs.Count < 1)
                {
                    Info("Loading all light's jobs...");
                    listLightJobs = GetObjectFromFile<List<LightJob>>(Common.LightJobsConfigFile);
                }

                return listLightJobs;
            }
            catch (Exception ex)
            {
                Bug("Load light jobs failed");
                Bug(ex.StackTrace);
                return null;
            }
        }

        public static LightEthernetControlBase GetLightByID(int controllerId)
        {
            try
            {
                if (listLightControllers.Count > 0 && listLightControllers != null)
                {
                    foreach (var controller in listLightControllers)
                    {
                        if (controller.IdControl == controllerId)
                        {
                            return controller;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Bug("Load light controller by Id failed");
                Bug(ex.StackTrace);
                return null;
            }
        }

        public static LightJob GetLightJobByID(int lightJobId)
        {
            try
            {
                if (listLightJobs.Count > 0 && listLightJobs != null)
                {
                    foreach (var lJob in listLightJobs)
                    {
                        if (lJob.LJobID == lightJobId)
                        {
                            return lJob;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Bug("Load light job by Id failed");
                Bug(ex.StackTrace);
                return null;
            }
        }
        #endregion



        public static void RunLightJob(bool isLightOn, CameraJob cJob)
        {
            if (cJob == null)
            {
                return;
            }
            if (!Settings.IsLightControl) return;
            foreach (var id in cJob.LightJobId)
            {
                var lJob = GetLightJobByID(id);
                if (isLightOn)
                {
                    lJob.TurnOn();
                }
                else
                {
                    lJob.TurnOff();
                }
            }
        }
        public static void RunLightJob(bool isLightOn, FunctionJob fJob)
        {
            if (fJob == null)
            {
                return;
            }
            if (!Settings.IsLightControl) return;
            foreach (var id in fJob.LightJobId)
            {
                var lJob = GetLightJobByID(id);
                if (isLightOn)
                {
                    lJob.TurnOn();
                }
                else
                {
                    lJob.TurnOff();
                }
            }
        }

        public static void RunLightJob(bool isLightOn, AlignJob cJob)
        {
            if (cJob == null)
            {
                return;
            }
            if (!Settings.IsLightControl) return;
            foreach (var id in cJob.LightJobId)
            {
                var lJob = GetLightJobByID(id);
                if (isLightOn)
                {
                    lJob.TurnOn();
                }
                else
                {
                    lJob.TurnOff();
                }
            }
        }

        public static void RunLightJob(bool isLightOn, IspJob cJob)
        {
            if (cJob == null)
            {
                return;
            }
            if (!Settings.IsLightControl) return;
            foreach (var id in cJob.LightJobId)
            {
                var lJob = GetLightJobByID(id);
                if (isLightOn)
                {
                    lJob.TurnOn();
                }
                else
                {
                    lJob.TurnOff();
                }
            }
        }

        // Light manual control (used when disabling automatic light control).
        // These methods intentionally do NOT check Settings.IsLightControl.
        public static void TurnOnAllLights()
        {
            try
            {
                // Ensure controllers are loaded (LightJob.TurnOn uses the bound controller).
                GetAllLights();
                var lightJobs = GetAllLightJobs();
                if (lightJobs == null) return;

                foreach (var lJob in lightJobs)
                {
                    if (lJob == null) continue;
                    // Re-bind controller in case jobs were loaded before controllers.
                    lJob.ControllerID = lJob.ControllerID;
                    lJob.TurnOn();
                }
            }
            catch (Exception ex)
            {
                Bug($"TurnOnAllLights failed: {ex.Message}");
                Bug(ex.StackTrace);
            }
        }

        public static void TurnOffAllLights()
        {
            try
            {
                // Also connects controllers inside GetAllLights().
                var controllers = GetAllLights();
                if (controllers == null) return;

                foreach (var controller in controllers)
                {
                    if (controller == null) continue;
                    controller.LightOffAll();
                }
            }
            catch (Exception ex)
            {
                Bug($"TurnOffAllLights failed: {ex.Message}");
                Bug(ex.StackTrace);
            }
        }

        public static int AddLightJob(LightJob lJob)
        {
            lJob.LJobID = listLightJobs.Count + 1;
            lJob.LJobName = "L_CH1";

            var saveResult = Common.SaveObjectToFile<List<LightJob>>(listLightJobs, LightJobsConfigFile);
            if (saveResult)
            {
                return lJob.LJobID;
            }

            return -1;
        }

        public static void LightingInit()
        {
            GetAllLights();
            GetAllLightJobs();
        }

        // runinspection with IO output
        public static async Task<bool> RunInspectionByIO(IspJob ispJob, IOJobs ioJob)
        {
            List<object> listResults = new List<object>();
            CameraJob inspectionCameraJob = null;
            listResults.Clear();

            var sw = Stopwatch.StartNew();
            bool isOk = false;

            try
            {
                if (ispJob == null)
                {
                    Bug("RunInspectionByIO: ispJob is null");
                    return false;
                }

                var camJob = GetCameraJob(ispJob.CamSettings.CameraId);
                inspectionCameraJob = camJob;
                if (camJob == null)
                {
                    Bug("RunInspectionByIO: Cannot find camera job for IspJob {0}", ispJob.Name);
                    return false;
                }

                CameraInfo(camJob, "Run Inspection IO: {0}", ispJob.Name);

                if (ioJob.LightJobId != null && ioJob.LightJobId.Length > 0)
                {
                    RunLightJob(true, ispJob);
                    CameraInfo(camJob, "{0} : Run Light job ", ispJob);

                }

                // Run camera job
                camJob.RunTool();
                if (camJob.RunStatus == CogToolResultConstants.Accept)
                {
                    CameraInfo(camJob, "RunInspectionByIO: Camera capture OK");
                    ispJob.InputImage = camJob.OutputImage;

                    // Run inspection job
                    ispJob.RunTool();
                    if (ispJob.RunStatus == CogToolResultConstants.Accept)
                    {
                        if (ispJob.Result != null)
                        {
                            if ((bool)ispJob.Result)
                            {
                                isOk = true;
                            }
                            else
                            {
                                isOk = false;
                            }
                        }
                        else
                        {
                            CameraBug(camJob, "RunInspectionByIO: IspJob result is null");
                            isOk = false;
                        }

                        listResults.Add(isOk);
                        var fileName = GetImageFileName(BuildImageName(ispJob.ProductID, ispJob.Name));

                        sw.Stop();
                        double CycleTime = sw.Elapsed.TotalSeconds;
                        CameraInfo(camJob, "CycleTime time: {0:F3} s", CycleTime);
                        UpdateData?.Invoke((int)ispJob.DisplayId, $"Time: {CycleTime:F3} s");

                        // Raise result event
                        ispJob.RaiseOnResult((int)ispJob.DisplayId, camJob.Watermark, fileName, (ICogImage)ispJob.OutputImage, listResults, isOk);


                        // Save image
                        OnJobDone(camJob.Watermark, fileName, (ICogImage)camJob.OutputImage, (ICogRecord)ispJob.Record, isOk, false);
                        if (isOk)
                        {
                            CameraInfo(camJob, "{0}: Inspection IO result OK", ispJob.Name);
                        }
                        else
                        {
                            CameraInfo(camJob, "{0}: Inspection IO result NG", ispJob.Name);
                        }
                    }
                    else
                    {
                        Bug("RunInspectionByIO: IspJob run failed, Status: {0}", ispJob.RunStatus);
                        CameraBugOnly(camJob, "RunInspectionByIO: IspJob run failed, Status: {0}", ispJob.RunStatus);
                        isOk = false;
                    }
                }
                else
                {
                    Bug("RunInspectionByIO: Camera job run failed, Status: {0}", camJob.RunStatus);
                    CameraBugOnly(camJob, "RunInspectionByIO: Camera job run failed, Status: {0}", camJob.RunStatus);
                    isOk = false;
                }

                // Turn off light
                if (ioJob.LightJobId != null && ioJob.LightJobId.Length > 0)
                {
                    RunLightJob(false, ispJob);
                    CameraInfo(camJob, "{0} : Off Light job ", ispJob);
                }
            }
            catch (Exception ex)
            {
                CameraBug(inspectionCameraJob, "RunInspectionByIO Exception: {0}", ex.Message);
                CameraBug(inspectionCameraJob, ex.StackTrace);
                isOk = false;
            }

            return isOk;
        }

        private static void CameraInfo(CameraJob cameraJob, string message)
        {
            Info(message);
            WriteCameraInfo(cameraJob, message);
        }

        private static void CameraInfo(CameraJob cameraJob, string format, params object[] values)
        {
            string message = string.Format(format, values);
            CameraInfo(cameraJob, message);
        }

        private static void CameraInfoOnly(CameraJob cameraJob, string message)
        {
            WriteCameraInfo(cameraJob, message);
        }

        private static void CameraInfoOnly(CameraJob cameraJob, string format, params object[] values)
        {
            if (cameraJob == null || string.IsNullOrWhiteSpace(cameraJob.Name))
            {
                return;
            }

            string message = string.Format(format, values);
            WriteCameraInfo(cameraJob, message);
        }

        private static void CameraBug(CameraJob cameraJob, string message)
        {
            Bug(message);
            WriteCameraBug(cameraJob, message);
        }

        private static void CameraBug(CameraJob cameraJob, string format, params object[] values)
        {
            string message = string.Format(format, values);
            CameraBug(cameraJob, message);
        }

        private static void CameraBugOnly(CameraJob cameraJob, string message)
        {
            WriteCameraBug(cameraJob, message);
        }

        private static void CameraBugOnly(CameraJob cameraJob, string format, params object[] values)
        {
            if (cameraJob == null || string.IsNullOrWhiteSpace(cameraJob.Name))
            {
                return;
            }

            string message = string.Format(format, values);
            WriteCameraBug(cameraJob, message);
        }

        private static void WriteCameraInfo(CameraJob cameraJob, string message)
        {
            if (cameraJob == null || string.IsNullOrWhiteSpace(cameraJob.Name))
            {
                return;
            }

            LOG(cameraJob.Name, message);
        }

        private static void WriteCameraBug(CameraJob cameraJob, string message)
        {
            if (cameraJob == null || string.IsNullOrWhiteSpace(cameraJob.Name))
            {
                return;
            }

            LogBug(cameraJob.Name, message);
        }

        private static string BuildImageName(string productId, string jobName)
        {
            var name = jobName.Replace(',', '_');
            return string.IsNullOrEmpty(productId) ? name : $"{productId}_{name}";
        }

        public static void SetOutPutData(FunctionJob job, PlcCam plc, CameraJob cameraJob = null)
        {
            if (job.ListDouble != null)
            {
                try
                {
                    var lstDouble = job.ListDouble as List<double>;
                    if (lstDouble != null && lstDouble.Count > 0)
                    {
                        plc.SetListDouble(lstDouble);
                        CameraInfo(cameraJob, "Data list double: {0}", string.Join(", ", lstDouble));
                    }
                }
                catch (Exception ex)
                {
                    CameraBug(cameraJob, "Cannot convert List double: {0}", ex.Message);
                }
            }

            if (job.ListInt != null)
            {
                try
                {
                    var lstInt = job.ListInt as List<int>;
                    if (lstInt != null && lstInt.Count > 0)
                    {
                        plc.SetListInt(lstInt);
                    }
                }
                catch (Exception ex)
                {
                    CameraBug(cameraJob, "Cannot convert List Int: {0}", ex.Message);
                }
            }

            if (job.StrOut1 != null)
            {
                try
                {
                    var str1 = job.StrOut1 as string;
                    if (!string.IsNullOrEmpty(str1))
                    {
                        plc.SetStrOut1(str1);
                    }
                }
                catch (Exception ex)
                {
                    CameraBug(cameraJob, "Cannot convert String output 1: {0}", ex.Message);
                }
            }

            if (job.StrOut2 != null)
            {
                try
                {
                    var str2 = job.StrOut2 as string;
                    if (!string.IsNullOrEmpty(str2))
                    {
                        plc.SetStrOut2(str2);
                    }
                }
                catch (Exception ex)
                {
                    CameraBug(cameraJob, "Cannot convert String output 2: {0}", ex.Message);
                }
            }

            if (job.StrOut3 != null)
            {
                try
                {
                    var str3 = job.StrOut3 as string;
                    if (!string.IsNullOrEmpty(str3))
                    {
                        plc.SetStrOut3(str3);
                    }
                }
                catch (Exception ex)
                {
                    CameraBug(cameraJob, "Cannot convert String output 3: {0}", ex.Message);
                }
            }
        }

        private static void OnJobDone(string strCamName, string strPrefix, ICogImage rawImage, ICogRecord record, bool isOK, bool isOverall, bool isSaveOnly = false)
        {
            if (isSaveOnly)//save from isp vidi job
            {
                LOG(strCamName, "Save image only function {0},{1}", strCamName, strPrefix);
                if (rawImage != null)
                {
                    //add image to save image queue
                    if (Common.Settings.SaveImage)
                    {
                        //raw image;
                        SaveImageModel img = new SaveImageModel();
                        img.IsRaw = true;
                        img.RawImage = rawImage;
                        img.FilePath = GetLoggingFolder(strCamName);
                        img.FileName = strPrefix;
                        img.Grade = isOK ? Grade.OK : Grade.NG;
                        //_qSaveImage.Enqueue(img);
                        SavingImgCtrl.AddImage(img);
                    }
                }
                return;
            }
            if (rawImage != null)
            {
                //add image to save image queue
                if (Common.Settings.SaveImage)
                {
                    //raw image;
                    SaveImageModel img = new SaveImageModel();
                    img.IsRaw = true;
                    img.RawImage = rawImage;
                    img.FilePath = GetLoggingFolder(strCamName);
                    img.FileName = strPrefix;
                    img.Grade = isOK ? Grade.OK : Grade.NG;
                    //_qSaveImage.Enqueue(img);
                    SavingImgCtrl.AddImage(img);
                }

                if (Common.Settings.SaveOverlayImage && record != null)
                {
                    if (_uiDispatcher != null)
                    {
                        _uiDispatcher.Invoke(() =>
                        {
                            try
                            {
                                lock (_cogDisplayLock)
                                {
                                    // Initialize cached display if needed
                                    InitializeCachedCogDisplay();

                                    // Use cached control if available and created
                                    if (_cachedCogDisplay != null && _cachedCogDisplay.Created && !_cachedCogDisplay.IsDisposed)
                                    {
                                        // Store original state to restore later
                                        var originalRecord = _cachedCogDisplay.Record;
                                        var originalImage = _cachedCogDisplay.Image;
                                        OverlayRecordSnapshot recordSnapshot = null;
                                        OverlayGraphicLineWidthScope lineWidthScope = null;
                                        System.Drawing.Image overlayBitmap = null;

                                        try
                                        {
                                            recordSnapshot = OverlayRecordSnapshot.Create(record, rawImage);
                                            _cachedCogDisplay.Image = rawImage;

                                            lineWidthScope = OverlayGraphicLineWidthScope.Apply(
                                                recordSnapshot.Record, Common.Settings.OverlayLineWidth);
                                            if (lineWidthScope.ModifiedCount == 0)
                                            {
                                                Bug("OnJobDone overlay record contains no adjustable graphics");
                                            }

                                            _cachedCogDisplay.Record = recordSnapshot.Record;

                                            overlayBitmap = _cachedCogDisplay.CreateContentBitmap(Cognex.VisionPro.Display.CogDisplayContentBitmapConstants.Image);
                                            //string fName = "";

                                            //if (isOverall)
                                            //{
                                            //    fName = DateTime.Now.ToString($"{strCamName}_{strPrefix}_ddMMyyyy_HHmmsstt");
                                            //}

                                            SaveImageModel img = new SaveImageModel
                                            {
                                                IsRaw = false,
                                                GraphicImg = overlayBitmap,
                                                FilePath = isOverall ? GetLoggingFolderVerify(lotId, "Verify") : GetLoggingFolder(strCamName),
                                                FileName = strPrefix,
                                                Grade = isOK ? Grade.OK : Grade.NG
                                            };
                                            SavingImgCtrl.AddImage(img);
                                            overlayBitmap = null; // SavingImgCtrl owns and disposes the bitmap after this point.
                                        }
                                        finally
                                        {
                                            try
                                            {
                                                _cachedCogDisplay.Record = originalRecord;
                                            }
                                            finally
                                            {
                                                try
                                                {
                                                    _cachedCogDisplay.Image = originalImage;
                                                }
                                                finally
                                                {
                                                    lineWidthScope?.Dispose();
                                                    recordSnapshot?.Dispose();
                                                    overlayBitmap?.Dispose();
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        Bug("CogRecordDisplay is not properly initialized or disposed");
                                    }
                                }
                            }
                            catch (InvalidActiveXStateException ex)
                            {
                                Bug($"ActiveX error in OnJobDone: {ex.Message}");
                                CleanupCachedCogDisplay();
                            }
                            catch (Exception ex)
                            {
                                Bug($"Error processing overlay image: {ex.Message}");
                            }
                        });
                    }
                }
                else if (Common.Settings.SaveOverlayImage)
                {
                    Bug("OnJobDone: record is null, cannot save overlay image");
                }

            }
            else
            {
                Bug("OnJobDone: _uiDispatcher is null, cannot save overlay image");
            }
        }
        public static void PublishUpdateData(int displayId, string dataSend)
        {
            UpdateData?.Invoke(displayId, dataSend);
        }

        #region Helper

        private static bool RunCameraWithRetry(CameraJob camJob, string context, int retryDelayMs = 90)
        {
            if (camJob == null) return false;
            camJob.RunTool();
            if (camJob.RunStatus == CogToolResultConstants.Accept && camJob.OutputImage != null)
            {
                CameraInfo(camJob, "{0}: Camera capture OK", context);
                return true;
            }
            // retry 1 lần cho case trigger miss nhưng không throw exception
            Thread.Sleep(retryDelayMs);
            camJob.RunTool();
            var ok = camJob.RunStatus == CogToolResultConstants.Accept && camJob.OutputImage != null;
            if (!ok)
            {
                Bug("{0}: camera capture failed after retry, cam={1}, status={2}", context, camJob.Name, camJob.RunStatus);
                CameraBugOnly(camJob, "{0}: camera capture failed after retry, cam={1}, status={2}", context, camJob.Name, camJob.RunStatus);
            }
            else
            {
                CameraInfo(camJob, "{0}: Camera capture OK after retry", context);
            }
            return ok;
        }
        public static string SplitByIndex(string input, int startIndex, int length)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            if (startIndex < 0 || length < 0 || startIndex + length > input.Length)
            {
                Info("Split SheetID: Start or length is out of range.");
                throw new ArgumentOutOfRangeException();
            }

            return input.Substring(startIndex, length);
        }

        public static void OnCogImageReceive(string readerIp, string path)
        {
            if (string.IsNullOrWhiteSpace(readerIp) || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (datamanImages.TryGetValue(readerIp, out var value))
            {
                datamanImages[readerIp] = path;
                Info($"Receive Dataman image IP: {readerIp}");
            }
            else
            {
                Info($"Ignored Dataman image for IP: {readerIp} (no active job waiting)");
            }
        }

        public static void AddDatamanImageQueue(string readerIp)
        {
            datamanImages[readerIp] = string.Empty;
        }
        public static CogRecordDisplay GetCachedCogDisplay()
        {
            return _cachedCogDisplay;
        }

        public static object GetCogDisplayLock()
        {
            return _cogDisplayLock;
        }

        public static Dispatcher GetUIDispatcher()
        {
            return _uiDispatcher;
        }
        public static CameraJob GetCameraJob(UInt32 CamId) => GetJobs<CameraJob>().FirstOrDefault(cam => cam.CamSettings.CameraId.Equals(CamId));

        /// <summary>Camera gán duy nhất cho một ô display (theo <see cref="BaseJob.DisplayId"/>).</summary>
        public static CameraJob GetCameraJobByDisplayId(int displayId)
        {
            List<CameraJob> matchingCameraJobs = GetJobs<CameraJob>(false)
                .Where(job => job != null && (int)job.DisplayId == displayId)
                .Take(2)
                .ToList();

            if (matchingCameraJobs.Count == 1)
            {
                return matchingCameraJobs[0];
            }

            if (matchingCameraJobs.Count > 1)
            {
                Bug(
                    "Multiple CameraJobs are configured for DisplayId {0}. Manual Grab and Solo Live are blocked.",
                    displayId);
            }

            return null;
        }

        public static CameraJob GetCameraJob(String strName) => GetJobs<CameraJob>().FirstOrDefault(cam => cam.IsMe(strName));

        public static CameraJob GetCameraJob(UInt32 CamId, String strName) => GetJobs<CameraJob>().FirstOrDefault(cam => cam.IsMe(strName) && cam.CamSettings.CameraId.Equals(CamId));

        public static AlignJob GetAlignJob(String strName) => GetJobs<AlignJob>().FirstOrDefault(job => job.IsMe(strName));

        public static HEJob GetHEJob(String strName) => GetJobs<HEJob>().FirstOrDefault(job => job.IsMe(strName));
        //public static FunctionJob GetAbstractJob(UInt32 toolId, UInt32 camId) => GetJobs<FunctionJob>().FirstOrDefault(job => job.ToolBlockId.Equals(toolId) && job.CamSettings.CameraId.Equals(camId));

        public static IspJob GetIspJob(String strName) => GetJobs<IspJob>().FirstOrDefault(job => job.IsMe(strName));

        public static FunctionJob GetFunctionJob(string strName) => GetJobs<FunctionJob>().FirstOrDefault(job => job.IsMe(strName));
        public static BaseJob GetJobByName(string strName) => GetJobs<BaseJob>().FirstOrDefault(job => job.IsMe(strName));
        public static List<T> GetJobs<T>(bool forceReload = false) where T : BaseJob => GetAllJobs(forceReload).FindAll(job => job is T).Cast<T>().ToList();

        //public static HEJob GetHEJob(int index)
        //{
        //    var jobs = GetJobs<HEJob>();
        //    if (index < 0 || index >= jobs.Count)
        //        return null;

        //    return jobs[index];
        //}
        public static HEJob GetHEJobByID(int id) => GetJobs<HEJob>().FirstOrDefault(job => job.CalibId == id);

        /// <summary>
        /// Runs a Vision Job by its 0-based index/ID in the job list and returns whether inspection passed (OK).
        /// Luồng thực thi viết tương tự RunInspectionByIO, lấy số lượng từ listDouble của SetOutPutData.
        /// </summary>
        public static async Task<bool> RunJobByIdAsync(int jobId)
        {
            return await Task.Run(() =>
            {
                List<object> listResults = new List<object>();
                CameraJob inspectionCameraJob = null;
                listResults.Clear();

                var sw = Stopwatch.StartNew();
                bool isOk = false;

                try
                {
                    var jobs = GetAllJobs(false);
                    if (jobs == null || jobId < 0 || jobId >= jobs.Count)
                    {
                        Bug("[JobController] Job ID {0} not found in listJobs (Total jobs: {1})", jobId, jobs?.Count ?? 0);
                        return false;
                    }

                    var targetJob = jobs[jobId];
                    if (targetJob == null)
                    {
                        Bug("[JobController] Job ID {0} is null", jobId);
                        return false;
                    }

                    CameraJob camJob = null;
                    IspJob ispJob = null;

                    if (targetJob is CameraJob cJob)
                    {
                        camJob = cJob;
                        ispJob = GetJobs<IspJob>().FirstOrDefault(j => j.CamSettings.CameraId == camJob.CamSettings.CameraId) ?? GetJobs<IspJob>().FirstOrDefault();
                    }
                    else if (targetJob is IspJob iJob)
                    {
                        ispJob = iJob;
                        camJob = GetCameraJob(ispJob.CamSettings.CameraId) ?? GetJobs<CameraJob>().FirstOrDefault();
                    }
                    else
                    {
                        ispJob = targetJob as IspJob ?? GetJobs<IspJob>().FirstOrDefault();
                        camJob = GetCameraJob(ispJob?.CamSettings?.CameraId ?? 0) ?? GetJobs<CameraJob>().FirstOrDefault();
                    }

                    if (ispJob == null)
                    {
                        Bug("RunJobByIdAsync: ispJob is null for Job ID {0}", jobId);
                        return false;
                    }

                    if (camJob == null)
                    {
                        camJob = GetCameraJob(ispJob.CamSettings.CameraId);
                    }
                    inspectionCameraJob = camJob;
                    if (camJob == null)
                    {
                        Bug("RunJobByIdAsync: Cannot find camera job for IspJob {0}", ispJob.Name);
                        return false;
                    }

                    CameraInfo(camJob, "Run Inspection: {0}", ispJob.Name);

                    if (ispJob.LightJobId != null && ispJob.LightJobId.Length > 0)
                    {
                        RunLightJob(true, ispJob);
                        CameraInfo(camJob, "{0} : Run Light job ", ispJob.Name);
                    }

                    // Run camera job
                    camJob.RunTool();
                    if (camJob.RunStatus == CogToolResultConstants.Accept)
                    {
                        CameraInfo(camJob, "RunJobByIdAsync: Camera capture OK");
                        ispJob.InputImage = camJob.OutputImage;

                        // Run inspection job
                        ispJob.RunTool();
                        if (ispJob.RunStatus == CogToolResultConstants.Accept)
                        {
                            if (ispJob.Result != null)
                            {
                                if ((bool)ispJob.Result)
                                {
                                    isOk = true;
                                }
                                else
                                {
                                    isOk = false;
                                }
                            }
                            else
                            {
                                CameraBug(camJob, "RunJobByIdAsync: IspJob result is null");
                                isOk = false;
                            }

                            // Xuất dữ liệu qua SetOutPutData
                            var plcCam = GetPlcCamByID(ispJob.PlcJobId);
                            SetOutPutData(ispJob, plcCam, camJob);

                            // Giá trị kết quả và số lượng đọc được lấy từ listDouble của SetOutPutData
                            listResults.Add(isOk);
                            if (ispJob.ListDouble != null)
                            {
                                try
                                {
                                    var lstDouble = ispJob.ListDouble as List<double>;
                                    if (lstDouble != null && lstDouble.Count > 0)
                                    {
                                        foreach (var d in lstDouble)
                                        {
                                            listResults.Add(d);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    CameraBug(camJob, "Cannot convert List double: {0}", ex.Message);
                                }
                            }

                            var fileName = GetImageFileName(BuildImageName(ispJob.ProductID, ispJob.Name));

                            sw.Stop();
                            double CycleTime = sw.Elapsed.TotalSeconds;
                            CameraInfo(camJob, "CycleTime time: {0:F3} s", CycleTime);

                            var lstDoubleForDisplay = ispJob.ListDouble as List<double>;
                            string qtyStr = (lstDoubleForDisplay != null && lstDoubleForDisplay.Count > 0) ? $"Qty: {string.Join(", ", lstDoubleForDisplay)} | " : "";
                            UpdateData?.Invoke((int)ispJob.DisplayId, $"{qtyStr}Time: {CycleTime:F3} s");

                            // Raise result event
                            ispJob.RaiseOnResult((int)ispJob.DisplayId, camJob.Watermark, fileName, (ICogImage)ispJob.OutputImage, listResults, isOk);

                            // Save image
                            OnJobDone(camJob.Watermark, fileName, (ICogImage)camJob.OutputImage, (ICogRecord)ispJob.Record, isOk, false);
                            if (isOk)
                            {
                                CameraInfo(camJob, "{0}: Inspection result OK", ispJob.Name);
                            }
                            else
                            {
                                CameraInfo(camJob, "{0}: Inspection result NG", ispJob.Name);
                            }
                        }
                        else
                        {
                            Bug("RunJobByIdAsync: IspJob run failed, Status: {0}", ispJob.RunStatus);
                            CameraBugOnly(camJob, "RunJobByIdAsync: IspJob run failed, Status: {0}", ispJob.RunStatus);
                            isOk = false;
                        }
                    }
                    else
                    {
                        Bug("RunJobByIdAsync: Camera job run failed, Status: {0}", camJob.RunStatus);
                        CameraBugOnly(camJob, "RunJobByIdAsync: Camera job run failed, Status: {0}", camJob.RunStatus);
                        isOk = false;
                    }

                    // Turn off light
                    if (ispJob.LightJobId != null && ispJob.LightJobId.Length > 0)
                    {
                        RunLightJob(false, ispJob);
                        CameraInfo(camJob, "{0} : Off Light job ", ispJob.Name);
                    }
                }
                catch (Exception ex)
                {
                    CameraBug(inspectionCameraJob, "RunJobByIdAsync Exception: {0}", ex.Message);
                    CameraBug(inspectionCameraJob, ex.StackTrace);
                    isOk = false;
                }

                return isOk;
            });
        }

        #endregion
    }
}
