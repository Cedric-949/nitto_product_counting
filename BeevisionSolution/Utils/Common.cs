using BeevisionSolution.Controller;
using BeevisionSolution.LocalDB;
using BeevisionSolution.Models;
using Cognex.VisionPro;
using Cognex.VisionPro.ImageFile;
using Cognex.VisionPro.ToolBlock;
using DocumentFormat.OpenXml.Wordprocessing;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using static BeevisionSolution.Utils.Constant;
using static System.Windows.Forms.AxHost;
using TPose = System.Tuple<double, double, double, bool>;

namespace BeevisionSolution.Utils
{
    static class Common
    {
        public static bool IsAutoMode = false;
        private const byte EncryptByte = 0x55;
        private static String[] arrToCopy = { "Configs", "Jobs", "Data" };
        private static string csv_data_log_file = "data";
        private static string csv_vidi_err_file = "vidi_err";

        private static BinaryFormatter Serializer = new BinaryFormatter();
        private static OperationMode _CurrentOperationMode = OperationMode.None;
        public static LayoutSetting LayoutSetting = new LayoutSetting();
        public static StatusManager StatusManager = new StatusManager();
        public static ResultModel ResultModel = new ResultModel();
        public static event EventHandler LayoutSettingChanged;

        internal static bool ConfiguredLog { get; set; } = false;
        internal static JsonSerializerSettings JsonPrivate = new JsonSerializerSettings { ContractResolver = new PrivateSetterContractResolver(), ReferenceLoopHandling = ReferenceLoopHandling.Ignore, ObjectCreationHandling = ObjectCreationHandling.Replace };
        internal static JsonSerializerSettings JsonAbstractJob = new JsonSerializerSettings { ContractResolver = new PrivateSetterContractResolver(), Converters = new JsonConverter[] { new VisionJobConverter() } };
        internal static JsonSerializerSettings JsonAbstractLight = new JsonSerializerSettings { ContractResolver = new PrivateSetterContractResolver(), Converters = new JsonConverter[] { new LightConverter() } };
        internal static AppSettings Settings = GetObjectFromFile<AppSettings>(AppConfigFile);
        internal static double DialogTitleFontSize = 32;
        internal static double DialogMessageFontSize = 22;
        internal static int MaxLogLines = 99;
        public static event OnOperationModeChanged OperationModeChanged;
        private static bool Zipping = false;
        private static DispatcherTimer _autoZipBackupTimer;

        internal static BvsLogger appLogger = new BvsLogger();
        public static Account CurrentUser { get; set; }
        internal static OperationMode CurrentOperationMode
        {
            get => _CurrentOperationMode;
            set
            {
                if (value != _CurrentOperationMode)
                {
                    _CurrentOperationMode = value;
                    OperationModeChanged?.Invoke(_CurrentOperationMode);
                }
            }
        }

        static Common()
        {
            CreateAllDirectory();
            StartMainLogger();
        }

        private static string StrDate => DateTime.Now.ToString("yyyy-MM-dd");

        internal static void StopAllLogger()
        {
            appLogger.StopAllLogger();
        }

        internal static void StartAllLogger()
        {
            StartMainLogger();
            StartAllCamerasLogger();
        }

        internal static void StartAllCamerasLogger()
        {
            foreach (var job in JobController.GetJobs<CameraJob>())
                appLogger.Start(job.Name);
        }

        internal static void StartMainLogger()
        {
            appLogger.Start(Settings.AppName, true);
            appLogger.StartCsv(csv_data_log_file);
            if (Settings.IsVidiRequired)
                appLogger.StartCsv(csv_vidi_err_file);
        }

        internal static void ConfigCameraLogger(string strLoggerName)
        {
            appLogger.Start(strLoggerName);
        }

        internal static CogPointMarker AddPointMarker(TPose p)
        {
            return new CogPointMarker()
            {
                GraphicType = CogPointMarkerGraphicTypeConstants.Crosshair,
                X = p.Item1,
                Y = p.Item2
            };
        }

        internal static CogPointMarker AddPointMarker(System.Drawing.Point p)
        {
            return new CogPointMarker()
            {
                GraphicType = CogPointMarkerGraphicTypeConstants.Crosshair,
                X = p.X,
                Y = p.Y
            };
        }
        internal static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) > 0 ? a : b;
        internal static T Min<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) > 0 ? b : a;
        internal static T Max<T>(T a, T b, T c) where T : IComparable<T> => Max(a, Max(b, c));
        internal static T Min<T>(T a, T b, T c) where T : IComparable<T> => Min(a, Min(b, c));

        internal static T ToEnum<T>(String strVal) where T : struct
        {
            T Result = default;
            Enum.TryParse(strVal, true, out Result);
            return Result;
        }

        internal static string GetExtension(ImageFormat fmt)
        {
            if (fmt == ImageFormat.Jpeg) return "jpg";
            if (fmt == ImageFormat.Tiff) return "tif";
            if (fmt == ImageFormat.Png) return "png";
            if (fmt == ImageFormat.Bmp) return "bmp";
            return fmt.ToString().ToLowerInvariant();
        }

        public static void SavePlainImage(String strPath, bool IsOk, String strFileName, ICogImage img)
        {
            //Info("Settings.SaveImage: {0}", Settings.SaveImage);
            if ((Settings.SaveImage) && (null != img))
            {
                strPath = CheckAndCreateDirectory(string.Format(@"{0}\Plain\{1}", strPath, (IsOk ? "OK" : "NG")));
                if (IsOk && Settings.SaveImageOK) SaveImage(strPath, strFileName, img, Settings.SavingImageFormat, Settings.ImageFactorOK);
                else if (!IsOk && Settings.SaveImageNG) SaveImage(strPath, strFileName, img, Settings.SavingImageFormat, Settings.ImageFactorNG);
            }
        }

        public static void SavePlainImage(String strPath, Grade grade, String strFileName, ICogImage img)
        {
            //Info("Settings.SaveImage: {0}", Settings.SaveImage);
            if ((Settings.SaveImage) && (null != img))
            {
                strPath = CheckAndCreateDirectory(string.Format(@"{0}\Plain\{1}", strPath, grade.ToString()));
                if ((Grade.OK == grade) && Settings.SaveImageOK) SaveImage(strPath, strFileName, img, Settings.SavingImageFormat, Settings.ImageFactorOK);
                else if ((Grade.OK != grade) && Settings.SaveImageNG) SaveImage(strPath, strFileName, img, Settings.SavingImageFormat, Settings.ImageFactorNG);
            }
        }


        private static void SaveImage(String strPath, String strFileName, ICogImage img, ImageFormat fmt, float scaleFactor = 1.0f)
        {
            ICogImage l_img = img;
            try
            {
                using (var file = new CogImageFile())
                {
                    if ((scaleFactor < 1.0f) && (scaleFactor > 0))
                        l_img = img.ScaleImage((int)(img.Width * scaleFactor), (int)(img.Height * scaleFactor));

                    file.Open(String.Format("{0}\\{1}.{2}", strPath, strFileName, GetExtension(fmt)), CogImageFileModeConstants.Write);
                    file.Append(l_img);
                    file.Close();
                }
            }
            catch (Exception ex) { Bug("Exception: {0}\nStackTrace: {1}", ex.Message, ex.StackTrace); }
            finally
            {
                if (l_img != null && !ReferenceEquals(l_img, img))
                {
                    try { (l_img as IDisposable)?.Dispose(); }
                    catch (Exception dex) { Bug("SaveImage dispose scaled image: {0}", dex.Message); }
                }
            }
        }

        public static void SaveImage(string strCamName, bool isOK, string strFileName, int step, ICogImage img, ICogRecord record )
        {
            if (img == null || !Common.Settings.SaveImage)
                return;

            try
            {
                string filePath = Common.GetLoggingFolder(strCamName);
                string fileName = Common.GetImageFileName(strFileName);

                // Save raw image
                SaveImageModel rawImg = new SaveImageModel();
                rawImg.IsRaw = true;
                rawImg.RawImage = img;
                rawImg.FilePath = filePath;
                rawImg.FileName = fileName;
                rawImg.Grade = isOK ? Grade.OK : Grade.NG;
                SavingImgCtrl.AddImage(rawImg);

                if (record != null && Common.Settings.SaveOverlayImage)
                {
                    var uiDispatcher = JobController.GetUIDispatcher();
                    if (uiDispatcher == null)
                    {
                        try
                        {
                            uiDispatcher = Application.Current?.Dispatcher;
                        }
                        catch
                        {
                            // Nếu Application.Current cũng null, không thể save overlay
                            Bug("UI Dispatcher not available for saving overlay image");
                            return;
                        }
                    }
                    if (uiDispatcher != null)
                    {
                        uiDispatcher.Invoke(() =>
                        {
                            try
                            {
                                var cogDisplayLock = JobController.GetCogDisplayLock();
                                lock (cogDisplayLock)
                                {
                                    // Initialize cached display if needed
                                    JobController.InitializeCachedCogDisplay();
                                    var cachedDisplay = JobController.GetCachedCogDisplay();

                                    if (cachedDisplay != null && cachedDisplay.Created && !cachedDisplay.IsDisposed)
                                    {
                                        // Store original state to restore later
                                        var originalRecord = cachedDisplay.Record;
                                        var originalImage = cachedDisplay.Image;
                                        OverlayRecordSnapshot recordSnapshot = null;
                                        OverlayGraphicLineWidthScope lineWidthScope = null;
                                        Image overlayBitmap = null;

                                        try
                                        {
                                            recordSnapshot = OverlayRecordSnapshot.Create(record, img);
                                            cachedDisplay.Image = img;

                                            lineWidthScope = OverlayGraphicLineWidthScope.Apply(
                                                recordSnapshot.Record, Common.Settings.OverlayLineWidth);
                                            if (lineWidthScope.ModifiedCount == 0)
                                            {
                                                Bug("SaveImage overlay record contains no adjustable graphics");
                                            }

                                            cachedDisplay.Record = recordSnapshot.Record;

                                            overlayBitmap = cachedDisplay.CreateContentBitmap(
                                                Cognex.VisionPro.Display.CogDisplayContentBitmapConstants.Image);

                                            if (overlayBitmap != null)
                                            {
                                                SaveImageModel overlayImg = new SaveImageModel
                                                {
                                                    IsRaw = false,
                                                    GraphicImg = overlayBitmap,
                                                    FilePath = filePath,
                                                    FileName = fileName,
                                                    Grade = isOK ? Grade.OK : Grade.NG
                                                };
                                                SavingImgCtrl.AddImage(overlayImg);
                                                overlayBitmap = null; // SavingImgCtrl owns and disposes the bitmap after this point.
                                            }
                                        }
                                        finally
                                        {
                                            try
                                            {
                                                cachedDisplay.Record = originalRecord;
                                            }
                                            finally
                                            {
                                                try
                                                {
                                                    cachedDisplay.Image = originalImage;
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
                                Bug($"ActiveX error in SaveImage overlay: {ex.Message}");
                            }
                            catch (Exception ex)
                            {
                                Bug($"Error saving overlay image: {ex.Message}");
                            }
                        });
                    }
                    else
                    {
                        Bug("UI Dispatcher not available for saving overlay image");
                    }
                }
            }
            catch (Exception ex)
            {
                Common.Bug("Error saving image in XT command: {0}", ex.Message);
            }
        }

        public static void SaveOverlayImage(String strPath, bool IsOk, String strFileName, Image img)
        {
            //Info("Settings.SaveOverlayImage: {0}, (null != img): {1}", Settings.SaveOverlayImage, (null != img));
            if ((Settings.SaveOverlayImage) && (null != img))
            {
                strPath = CheckAndCreateDirectory(string.Format(@"{0}\Overlay\{1}", strPath, (IsOk ? "OK" : "NG")));
                if (IsOk && Settings.SaveOverlayImageOK) SaveOverlayImage(strPath, strFileName, img);
                else if (!IsOk && Settings.SaveOverlayImageNG) SaveOverlayImage(strPath, strFileName, img);
            }
        }

        public static void SaveOverlayImage(String strPath, Grade grade, String strFileName, Image img)
        {
            //Info("Settings.SaveOverlayImage: {0}, (null != img): {1}", Settings.SaveOverlayImage, (null != img));
            if ((Settings.SaveOverlayImage) && (null != img))
            {
                strPath = CheckAndCreateDirectory(string.Format(@"{0}\Overlay\{1}", strPath, grade.ToString()));
                if ((Grade.OK == grade) && Settings.SaveOverlayImageOK) SaveOverlayImage(strPath, strFileName, img);
                else if ((Grade.OK != grade) && Settings.SaveOverlayImageNG) SaveOverlayImage(strPath, strFileName, img);
            }
        }

        public static void SaveOverlayImageBarcode(String strPath, Grade grade, String strFileName, Image img)
        {
            //Info("Settings.SaveOverlayImage: {0}, (null != img): {1}", Settings.SaveOverlayImage, (null != img));
            if ((Settings.SaveOverlayImage) && (null != img))
            {
                strPath = CheckAndCreateDirectory(string.Format(@"{0}\Raw\{1}", strPath, grade.ToString()));
                if ((Grade.OK == grade) && Settings.SaveOverlayImageOK) SaveOverlayImage(strPath, strFileName, img);
                else if ((Grade.OK != grade) && Settings.SaveOverlayImageNG) SaveOverlayImage(strPath, strFileName, img);
            }
        }


        private static void SaveOverlayImage(String strPath, String strFileName, Image img)
        {

            try { img.Save(string.Format("{0}\\{1}.jpg", strPath, strFileName), ImageFormat.Jpeg); }
            catch (Exception ex) { Bug("Exception: {0}\nStackTrace: {1}", ex.Message, ex.StackTrace); }

        }

        internal static string GetLoggingFolder(int CamId) => CheckAndCreateDirectory(string.Format(@"{0}\\Cam{1}\{2:D2}", TodayLoggingPath, CamId, DateTime.Now.Hour));
        //internal static string GetLoggingFolder(string strCamName) => CheckAndCreateDirectory(string.Format(@"{0}\\{1}\{2:D2}", TodayLoggingPath, strCamName, DateTime.Now.Hour));
        internal static string GetLoggingFolderVerify(string strLot, string folderName) => CheckAndCreateDirectory(string.Format(@"{0}\{1}\{2}", TodayLoggingPath, strLot, folderName));
        internal static string GetLoggingFolderBarcode(string strCamName, string strLot) => CheckAndCreateDirectory(string.Format(@"{0}\{1}\Barcode\{2}", TodayLoggingPath, strLot, strCamName));
        internal static string GetLoggingFolder(string strCamName, string strCmd) => CheckAndCreateDirectory(string.Format(@"{0}\\{1}\{2:D2}\{3}", TodayLoggingPath, strCamName, DateTime.Now.Hour, strCmd));
        internal static string GetImageFileName(string strIdCode) => String.Format("{0}_{1}", strIdCode, DateTime.Now.ToString("HH_mm_ss_fff"));
        internal static string TodayLoggingPath => string.Format(@"{0}\{1}", Settings.LoggingDirectory, StrDate);

        internal static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Unknown";
            name = name.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrEmpty(name) ? "Unknown" : name;
        }

        internal static string CpkDataRootFolder =>
            CheckAndCreateDirectory(Path.Combine(Settings.LoggingDirectory, "DataCPK"));

        internal static string GetCpkExcelFilePath(string jobName) =>
            Path.Combine(
                CheckAndCreateDirectory(Path.Combine(CpkDataRootFolder, StrDate)),
                SanitizeFileName(jobName) + ".xlsx");

        internal static string GetCpkCombinedReportPath() =>
            Path.Combine(
                CheckAndCreateDirectory(Path.Combine(CpkDataRootFolder, StrDate)),
                $"CPK_Report_{DateTime.Now:HHmmss}.xlsx");

        internal static string GetLoggingFolder(string strCamName)
        {
            return CheckAndCreateDirectory(string.Format(@"{0}\\{1}\{2:D2}", TodayLoggingPath, SanitizeFileName(strCamName), DateTime.Now.Hour));
        }
        internal static string GetDataHistoryPath() => string.Format(@"{0}\{1}.csv", TodayLoggingPath, csv_data_log_file);
        internal static string GetSavedOverlayImagePath() => string.Format(@"{0}\Cam1\Overlay\NG", TodayLoggingPath);
        internal static void MemoryCleanup()
        {
            GC.Collect();
            //GC.WaitForPendingFinalizers();
            //GC.Collect();
        }



        internal static CogToolBlock LoadToolBlock(String strPath, bool encrypt = true)
        {
            Object tb = new CogToolBlock();

            if (String.IsNullOrEmpty(strPath))
                return (CogToolBlock)tb;

            if (!(strPath[1] == ':' && strPath[2] == '\\')) // Relative or absolute Path
                strPath = String.Format("{0}\\{1}", ProfileFolder, strPath);

            if (File.Exists(strPath))
            {
                bool isEncrypted = false;
                try
                {
                    // Ưu tiên load qua API Cognex để tối ưu RAM, tránh tạo buffer trung gian
                    var obj = CogSerializer.LoadObjectFromFile(strPath);
                    if (obj is CogToolBlock)
                    {
                        tb = obj;
                    }
                    else
                    {
                        // File mã hóa theo chuẩn cũ bị méo header, làm thư viện nhận diện nhầm thành object khác (vd: CogDataBinding)
                        isEncrypted = true;
                    }
                }
                catch
                {
                    // Fallback: nếu lỗi do sai định dạng, bật cờ đọc kiểu cũ
                    isEncrypted = true;
                }

                if (isEncrypted)
                {
                    // Đọc tương thích ngược
                    using (var fs = new FileStream(strPath, FileMode.Open))
                    {
                        byte[] buffer = new byte[fs.Length];
                        fs.Read(buffer, 0, buffer.Length);
                        // Giải mã theo chuẩn cũ bằng cách đảo bit XOR tại byte vị trí index 1
                        if (encrypt) buffer[1] ^= EncryptByte;
                        using (var ms = new MemoryStream(buffer))
                        {
                            try
                            {
                                ms.Position = 0;
                                var obj = Serializer.Deserialize(ms);
                                if (null != obj) tb = obj;
                            }
                            catch (Exception ex) { Bug("Exception: {0}\nStackTrace: {1}", ex.Message, ex.StackTrace); }
                        }
                        buffer = null;
                    }
                }
            }
            return (CogToolBlock)tb;
        }

        //internal static Task<CogToolBlock> DeepCopy(Object obj) => Task.FromResult((CogToolBlock)CogSerializer.DeepCopyObject((CogToolBlock)obj));

        internal static bool SaveToolBlock(Object tb, String strPath, bool encrypt = true)
        {
            bool status = true;
            if (String.IsNullOrEmpty(strPath))
                return false;

            if (!(strPath[1] == ':' && strPath[2] == '\\'))
                strPath = String.Format("{0}\\{1}", ProfileFolder, strPath);

            try
            {
                // Bỏ qua mã hóa, luôn dùng API chuẩn để stream xuống đĩa chống OOM
                CogSerializer.SaveObjectToFile(tb, strPath, typeof(BinaryFormatter), CogSerializationOptionsConstants.Minimum);
            }
            catch { status = false; }
            return status;
        }

        public static void CreateZipBackup()
        {
            if (Zipping)
            {
                Info("Another backup is in progress");
                return;
            }

            var strBackupDir = CheckAndCreateDirectory(String.Format("{0}\\Backups", BaseProfilesFolder));
            var src = String.Format("{0}\\{1}", BaseProfilesFolder, Settings.CurrentProfile);

            Task.Run(() =>
            {
                var fn = "";
                var idx = 0;
                Zipping = true;
                do
                {
                    fn = CreateZipFile(strBackupDir, idx++);
                }
                while (File.Exists(fn));

                Info("Creating zip backup");
                try
                {
                    //var fz = new FastZip();
                    //fz.CreateZip(fn, src, true, null);
                    ZipFile.CreateFromDirectory(src, fn, CompressionLevel.Fastest, true);
                }
                catch { }
                Info("Zip backup finished");
                Zipping = false;
            });
        }

        /// Khởi tạo timer để kiểm tra và tạo zip backup tự động theo khoảng thời gian cấu hình
        public static void InitAutoZipBackup()
        {
            try
            {
                if (_autoZipBackupTimer != null)
                {
                    _autoZipBackupTimer.Stop();
                    _autoZipBackupTimer = null;
                }
                
                if (!Settings.EnableAutoZipBackup || Settings.ZipBackupIntervalDays <= 0)
                {
                    Info("Auto Zip Backup is disabled or invalid interval");
                    return;
                }

                // Tạo timer mới, kiểm tra mỗi giờ (3600000 ms)
                _autoZipBackupTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromHours(1)
                };
                _autoZipBackupTimer.Tick += AutoZipBackupTimer_Tick;
                _autoZipBackupTimer.Start();

                Info("Auto Zip Backup initialized - Interval: {0} days, Check every hour", Settings.ZipBackupIntervalDays);

                // chek
                CheckAndCreateZipBackup();
            }
            catch (Exception ex)
            {
                Bug("Error initializing Auto Zip Backup: {0}", ex.Message);
            }
        }

        /// Check and create zip backup 
        private static void CheckAndCreateZipBackup()
        {
            try
            {
                if (!Settings.EnableAutoZipBackup)
                {
                    return;
                }

                if (Settings.ZipBackupIntervalDays <= 0)
                {
                    Info("Auto Zip Backup interval is invalid: {0} days", Settings.ZipBackupIntervalDays);
                    return;
                }

                DateTime now = DateTime.Now;
                DateTime? lastBackup = Settings.LastZipBackupTime;

                // Nếu chưa backup
                if (!lastBackup.HasValue)
                {
                    Info("No previous backup found, creating first auto backup...");
                    CreateZipBackupAndUpdateTime();
                }
                else
                {
                    TimeSpan timeSinceLastBackup = now - lastBackup.Value;
                    int daysSinceLastBackup = (int)timeSinceLastBackup.TotalDays;

                    if (daysSinceLastBackup >= Settings.ZipBackupIntervalDays)
                    {
                        Info("Auto Zip Backup triggered - Last backup: {0} days ago (Threshold: {1} days)",
                            daysSinceLastBackup, Settings.ZipBackupIntervalDays);
                        CreateZipBackupAndUpdateTime();
                    }
                    else
                    {
                        int daysRemaining = Settings.ZipBackupIntervalDays - daysSinceLastBackup;
                        Info("Auto Zip Backup - Next backup in {0} days (Last: {1})",
                            daysRemaining, lastBackup.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                    }
                }
            }
            catch (Exception ex)
            {
                Bug("Error checking Auto Zip Backup: {0}", ex.Message);
            }
        }

        /// Tạo zip backup và cập nhật thời gian backup
        private static void CreateZipBackupAndUpdateTime()
        {
            try
            {
                Info("Starting scheduled auto zip backup...");
                CreateZipBackup();

                // Cập nhật thời gian backup cuối cùng
                Settings.LastZipBackupTime = DateTime.Now;
                Settings.Save();

                Info("Auto zip backup completed at {0}", Settings.LastZipBackupTime.Value.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch (Exception ex)
            {
                Bug("Error creating auto zip backup: {0}", ex.Message);
            }
        }

        /// Event handler cho timer
        private static void AutoZipBackupTimer_Tick(object sender, EventArgs e)
        {
            CheckAndCreateZipBackup();
        }

        private static string CreateZipFile(string strBackupDir, int version)
        {
            var now = DateTime.Now;
            return String.Format("{0}\\{1}_{2:D2}{3:D2}.{4:D2}.zip", strBackupDir, Settings.CurrentProfile, now.Month, now.Day, version);
        }

        private static bool Copying = false;
        public async static void CopyDir(string sourceDirectory, string targetDirectory)
        {
            while (Copying) await Task.Delay(1);

            await Task.Run(() =>
            {
                Copying = true;
                var srcDir = String.Format("{0}\\{1}", BaseProfilesFolder, sourceDirectory);
                var desDir = CheckAndCreateDirectory(String.Format("{0}\\{1}", BaseProfilesFolder, targetDirectory));
                var diSource = new DirectoryInfo(srcDir);
                var diTarget = new DirectoryInfo(desDir);

                foreach (DirectoryInfo diSourceSubDir in diSource.GetDirectories())
                {
                    if (arrToCopy.Contains(diSourceSubDir.Name))
                    {
                        DirectoryInfo nextTargetSubDir = diTarget.CreateSubdirectory(diSourceSubDir.Name);
                        CopyAll(diSourceSubDir, nextTargetSubDir);
                    }
                }
                Copying = false;
            });
        }

        public static void CopyAll(DirectoryInfo source, DirectoryInfo target)
        {
            Directory.CreateDirectory(target.FullName);

            // Copy each file into the new directory.
            foreach (FileInfo fi in source.GetFiles())
            {
                fi.CopyTo(Path.Combine(target.FullName, fi.Name), true);
            }

            foreach (DirectoryInfo diSourceSubDir in source.GetDirectories())
            {
                DirectoryInfo nextTargetSubDir = target.CreateSubdirectory(diSourceSubDir.Name);
                CopyAll(diSourceSubDir, nextTargetSubDir);
            }
        }

        internal static T GetObjectFromFile<T>(String strFilePath) where T : new()
        {
            return GetObjectFromFile<T>(strFilePath, JsonPrivate);
        }

        internal static T GetObjectFromFile<T>(String strFilePath, JsonSerializerSettings settings) where T : new()
        {
            if (File.Exists(strFilePath))
            {
                var txt = File.ReadAllText(strFilePath);
                return JsonConvert.DeserializeObject<T>(txt, settings);
            }
            return (new T());
        }

        internal static bool SaveObjectToFile<T>(T obj, String strFilePath)
        {
            return SaveObjectToFile<T>(obj, strFilePath, JsonPrivate);
        }

        internal static bool SaveObjectToFile<T>(T obj, String strFilePath, JsonSerializerSettings settings)
        {
            try
            {
                File.WriteAllText(strFilePath, JsonConvert.SerializeObject(obj, Formatting.Indented, settings));
            }
            catch (Exception ex)
            {
                Bug("SaveObjectToFile failed for path: {0}. Error: {1}", strFilePath, ex.Message);
                return false;
            }
            return true;
        }

        internal static string BaseStorageDirectory => string.Format("{0}\\BeeVision", Environment.GetFolderPath(Environment.SpecialFolder.Personal));
        internal static string BaseProfilesFolder => string.Format("{0}\\Profiles", Settings.RootDirectory);
        internal static string ProfileFolder => string.Format("{0}\\{1}", BaseProfilesFolder, Settings.CurrentProfile);

        public static List<string> GetAllProfiles()
        {
            var lstProfiles = new List<string>();
            foreach (var s in Directory.GetDirectories(BaseProfilesFolder))
            {
                var pro = Path.GetFileName(s);
                if (!pro.Equals("Backups"))
                    lstProfiles.Add(pro);
            }
            if (lstProfiles.Count == 0)
                lstProfiles.Add("Default");
            lstProfiles.Sort((a, b) => a.CompareTo(b));
            return lstProfiles;
        }

        internal static string DataFolder => string.Format("{0}\\Data", ProfileFolder);
        internal static string JobsFolder => string.Format("{0}\\Jobs", ProfileFolder);
        internal static string ConfigFolder => string.Format("{0}\\Configs", ProfileFolder);
        internal static string LogsFolder => string.Format("{0}\\Logs", ProfileFolder);

        internal static void CreateAllDirectory()
        {
            CheckAndCreateDirectory(BaseStorageDirectory);
            CheckAndCreateDirectory(BaseProfilesFolder);
            CheckAndCreateDirectory(ProfileFolder);
            CheckAndCreateDirectory(ConfigFolder);
            CheckAndCreateDirectory(JobsFolder);
            CheckAndCreateDirectory(DataFolder);
        }

        internal static string AppConfigFile
        {
            get
            {
                if (!File.Exists(APP_FILE_SETTING))
                {
                    SaveObjectToFile(AppSettings.Default, APP_FILE_SETTING);
                }
                return APP_FILE_SETTING;
            }
        }

        internal static string HashTableFile => String.Format("{0}\\{1}", ConfigFolder, "hashdata.json");
        internal static string BumjinListItemFile => String.Format("{0}\\{1}", ConfigFolder, "bumjin.json");
        internal static string PcsRotationConfigFile => String.Format("{0}\\{1}", ConfigFolder, "PcsConfig.json");
        internal static string JobsConfigFile => String.Format("{0}\\{1}", ConfigFolder, JOB_FILE_SETTING);
        internal static string PLCsConfigFile => String.Format("{0}\\{1}", ConfigFolder, PLCS_FILE_SETTING);
        internal static string PlcCamerasConfigFile => String.Format("{0}\\{1}", ConfigFolder, PLCCAM_FILE_SETTING);
        internal static string PlcCameraSettingFile => String.Format("{0}\\{1}", ConfigFolder, PLC_CAMERA_SETTING_FILE);
        internal static string CalibsConfigFile => String.Format("{0}\\{1}", DataFolder, CALIB_FILE_SETTING);
        internal static string CalibsCamMovingConfigFile => String.Format("{0}\\{1}", DataFolder, CALIB_CAM_MOVING_FILE_SETTING);
        internal static string TrainedPointsConfigFile => String.Format("{0}\\{1}", DataFolder, TRAINED_FILE_SETTING);
        internal static string TrainedPointsTTMConfigFile => String.Format("{0}\\{1}", DataFolder, TRAINED_TTM_FILE_SETTING);
        internal static string SystemCommandConfigFile => String.Format("{0}\\{1}", ConfigFolder, SYSTEM_COMMAND_FILE_SETTING);
        internal static string SystemCommandConfigFile2 => String.Format("{0}\\{1}", ConfigFolder, SYSTEM_COMMAND_FILE_SETTING2);
        internal static string LightsConfigFile => String.Format("{0}\\{1}", ConfigFolder, LIGHTS_FILE_SETTING);
        internal static string LightJobsConfigFile => String.Format("{0}\\{1}", ConfigFolder, LIGHTS_JOB_FILE_SETTING);
        internal static string VidiWorkspaceFile => String.Format("{0}\\main.vrws", DataFolder);
        internal static string VisionMasterPosesConfigFile => String.Format("{0}\\{1}", DataFolder, VISION_MASTER_POSES_FILE_SETTING);
        internal static string IOConfigFile => String.Format("{0}\\{1}", ConfigFolder, IO_CONFIG_FILE_SETTING);
        internal static string IOJobsConfigFile => String.Format("{0}\\{1}", ConfigFolder, IO_JOBS_CONFIG_FILE_SETTING);
        internal static string CpkConfigFile => String.Format("{0}\\{1}", ConfigFolder, CPK_CONFIG_FILE_SETTING);
        internal static string MeasureDistanceConfigFile => String.Format("{0}\\{1}", ConfigFolder, "MeasureDistanceConfig.json");
        internal static string MotionConfigFile => String.Format("{0}\\{1}", ConfigFolder, "motion_config.json");


        internal static String StdFormat(Object obj)
        {
            //if (obj is float fVal)
            //    return StdFormat(fVal);

            if (obj is double dVal)
                return StdFormat(dVal);

            if (obj is BeeLib.Math.Pose poseVal)
                return StdFormat(poseVal);

            if (obj is BeeLib.Math.Point pointVal)
                return StdFormat(pointVal);

            //if (obj is CppPose cppPoseVal)
            //    return StdFormat(cppPoseVal);

            //if (obj is CppPoint cppPointVal)
            //    return StdFormat(cppPointVal);

            return obj?.ToString();
        }

        internal static double Compensation(double val)
        {
            while (val > 360)
                val -= 360;
            while (val < -360)
                val += 360;
            if (val > 180)
                val -= 360;
            else if (val < -180)
                val += 360;

            return val;
        }

        internal static String StdFormat(double val) => double.IsNaN(val) ? "NaN" : String.Format("{0:F5}", val).Substring(0, 7);

        internal static string StdFormat(BeeLib.Math.Pose p) => String.Format("{0},{1},{2}", StdFormat(p.X), StdFormat(p.Y), StdFormat(Compensation(p.Th)));
        internal static string StdFormat(BeeLib.Math.Point p) => String.Format("{0},{1}", StdFormat(p.X), StdFormat(p.Y));

        //internal static string StdFormat(CppPose p) => String.Format("{0},{1},{2}", StdFormat(p.X), StdFormat(p.Y), StdFormat(Compensation(p.Th)));
        //internal static string StdFormat(CppPoint p) => String.Format("{0},{1}", StdFormat(p.X), StdFormat(p.Y));

        internal static string CheckAndCreateDirectory(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        public static void BUG(string strLog) => System.Diagnostics.Debug.WriteLine(strLog);
        public static void BUG(String fmt, params object[] objs) => BUG(string.Format(fmt, objs));

        internal static void Info(String str) => appLogger.AddLog(new LogItem() { Entry = str, LogName = Settings.AppName });
        internal static void Info(String fmt, params object[] objs) => Info(String.Format(fmt, objs));

        internal static void Bug(String str) => appLogger.AddLog(new LogItem() { Entry = str, LogName = Settings.AppName, Level = LogLevel.BUG });
        internal static void Bug(String fmt, params object[] objs) => Bug(String.Format(fmt, objs));

        internal static void LOG(string strLogName, string strLogEntry) => appLogger.AddLog(new LogItem() { Entry = strLogEntry, LogName = strLogName });
        internal static void LOG(string strLogName, string fmt, params object[] objs) => LOG(strLogName, String.Format(fmt, objs));

        internal static void LogBug(string logName, string logEntry) => appLogger.AddLog(new LogItem() { Entry = logEntry, LogName = logName, Level = LogLevel.BUG });
        internal static void LogBug(string logName, string format, params object[] values) => LogBug(logName, String.Format(format, values));

        internal static void LogDataCsv(string str) => appLogger.AddLog(new LogItem() { Entry = str, LogName = csv_data_log_file, Level = LogLevel.CSV });
        internal static void LogVidiErrCsv(string str)
        {
            if (Settings.IsVidiRequired)
            {
                appLogger.AddLog(new LogItem() { Entry = str, LogName = csv_vidi_err_file, Level = LogLevel.CSV });
            }
        }
        internal static void LogDataCsvByCode(string code)
        {
            HistoryDataModel m = new HistoryDataModel(code);
            appLogger.AddLog(new LogItem() { Entry = m.GetRawData(), LogName = csv_data_log_file, Level = LogLevel.CSV });
        }

        public static void LoadLayoutSettings()
        {
            string jobConfigFilePath = String.Format("{0}\\{1}", ConfigFolder, LAYOUT_SETTING_FILE);
            if (!File.Exists(jobConfigFilePath))
            {
                LayoutSetting = new LayoutSetting();
                Info("Layout settings file not found");
            }
            else
            {
                object loadedConfig = LoadBinaryFile(jobConfigFilePath);
                if (loadedConfig != null)
                {
                    LayoutSetting = (LayoutSetting)loadedConfig;
                    Info("Loaded {0} layout settings", LayoutSetting);
                }
                else
                {
                    Info("Failed to load layout settings, creating default settings");
                }
            }
        }
        public static void SaveLayoutSettings()
        {
            string layoutConfigFilePath = String.Format("{0}\\{1}", ConfigFolder, LAYOUT_SETTING_FILE);
            Info("Saving layout settings to: {0}", layoutConfigFilePath);
            SaveBinaryFile(layoutConfigFilePath, LayoutSetting);
            LayoutSettingChanged?.Invoke(null, EventArgs.Empty);
        }
        public static void LoadResultSettings()
        {
            string jobConfigFilePath = String.Format("{0}\\{1}", ConfigFolder, RESULT_FILE);
            if (!File.Exists(jobConfigFilePath))
            {
                ResultModel = new ResultModel();
                Info("Result settings file not found");
            }
            else
            {
                object loadedConfig = LoadBinaryFile(jobConfigFilePath);
                if (loadedConfig != null)
                {
                    ResultModel = (ResultModel)loadedConfig;
                    Info("Loaded {0} result settings", ResultModel);
                }
                else
                {
                    Info("Failed to load result settings, creating default settings");
                }
            }
        }
        public static void SaveResultSettings()
        {
            string resulFilePath = String.Format("{0}\\{1}", ConfigFolder, RESULT_FILE);
            Info("Saving result settings to: {0}", resulFilePath);
            SaveBinaryFile(resulFilePath, ResultModel);
            //LayoutSettingChanged?.Invoke(null, EventArgs.Empty);
        }
        private static void SaveBinaryFile(string fullFileName, object data)
        {
            Stream ms = File.OpenWrite(fullFileName);
            BinaryFormatter formatter = new BinaryFormatter();
            formatter.Serialize(ms, data);

            ms.Flush();
            ms.Close();
            ms.Dispose();
        }

        private static object LoadBinaryFile(string fullFileName)
        {
            try
            {
                //Format the object as Binary  
                BinaryFormatter formatter = new BinaryFormatter();

                //Reading the file from the server  
                FileStream fs = File.Open(fullFileName, FileMode.Open);
                object obj = formatter.Deserialize(fs);
                fs.Flush();
                fs.Close();
                fs.Dispose();

                return obj;
            }
            catch (Exception ex)
            {
                BUG("LoadBinaryFile error: {0}", ex.Message);
                return null;
            }

        }
    }
}

#region Trash
//internal static Guid AppGuid
//{
//    get => new Guid((Assembly.GetEntryAssembly().GetCustomAttributes(typeof(GuidAttribute), true)[0] as GuidAttribute).Value);
//}

//internal static Guid AssemblyGuid
//{
//    get => new Guid((Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(GuidAttribute), true)[0] as GuidAttribute).Value);
//}

//internal static string AllUsersDataFolder
//{
//    get
//    {
//        Guid appGuid = Common.AppGuid;
//        string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
//        return Common.CheckDir(string.Format("{0}\\{1}\\", folderPath, appGuid.ToString("B").ToUpper()));
//    }
//}

//internal static string UserDataFolder
//{
//    get
//    {
//        Guid appGuid = Common.AppGuid;
//        string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
//        return Common.CheckDir(string.Format("{0}\\{1}\\", folderPath, appGuid.ToString("B").ToUpper()));
//    }
//}

//internal static string UserRoamingDataFolder
//{
//    get
//    {
//        Guid appGuid = Common.AppGuid;
//        string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
//        return Common.CheckDir(string.Format("{0}\\{1}\\", folderPath, appGuid.ToString("B").ToUpper()));
//    }
//}
#endregion
