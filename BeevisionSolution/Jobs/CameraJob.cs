using Cognex.VisionPro;
using Cognex.VisionPro.ToolBlock;
using Newtonsoft.Json;
using System;
using System.Threading;
using System.Threading.Tasks;
using static BeevisionSolution.Utils.Constant;
using static BeevisionSolution.Utils.Common;

using BeevisionSolution.Utils;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Drawing;
using Cognex.VisionPro.ImageFile;
using System.Runtime.InteropServices.WindowsRuntime;
using Basler.Pylon;
using System.Diagnostics;
using System.Windows.Forms;
using BeevisionSolution.Controller;
using BeevisionSolution.Jobs.CameraHandlers;
using DocumentFormat.OpenXml.Drawing;

namespace BeevisionSolution.Models
{
    [Serializable]
    public class CameraJob : FunctionJob 
    {
        [DllImport("Kernel32.dll", EntryPoint = "RtlMoveMemory", CharSet = CharSet.Ansi)]
        internal static extern void CopyMemory(IntPtr pDst, IntPtr pSrc, int len);
        private static int Timeout = 3000;

        public Double Angle { get; set; } = 10;
        public int RotationSteps { get; set; } = 2;
        public int GridSize { get; set; } = 3;
        public string Watermark { get; set; } = "BeeVision Solution";
        [JsonIgnore]
        public object LastValidImage { get; internal set; } = null;

        public int CamType { get; set; } = 0;//0: VPP, 1/2: IRayple linescan, 3: Basler CXP, 4: webcam, 5: ITEK area scan
        public uint BoardNo { get; set; } = 0;//No of framegrabber, use in case CamType = 1
        public int CamLSNo { get; set; } = 0;//No of Linescan camera
        public uint CamLSTimeOut { get; set; } = 1000;//milisecond, time out for get a single frame
        public long WidthLS { get; set; } = 8192;
        public long HeightLS { get; set; } = 5000;
        public double ExpLS { get; set; } = 5;
        public int GainLS { get; set; } = 1;
        /// <summary>Capture card .mvcfg → load qua CardDev (vd. cardkkk.mvcfg).</summary>
        public string CardIrayCfgPath { get; set; } = "";
        /// <summary>Linescan camera .mvcfg → load qua CamDev (vd. camkkk.mvcfg).</summary>
        public string CamIrayCfgPath { get; set; } = "";
        public uint ItekDeviceIndex { get; set; } = 0;
        public string ItekSerialNumber { get; set; } = "";
        public int ItekGrabTimeoutMs { get; set; } = 5000;
        public int ItekBufferCount { get; set; } = 2;
        public string ItekBoardConfigPath { get; set; } = "";
        public string ItekCameraUserSet { get; set; } = "";
        public bool ItekExpectedColor { get; set; } = false;
        public bool IsLightTrigger { get; set; }
        public bool UseCameraDmCode { get; set; } = false;
        public string dmCode { get; set; } = "";
        public BeeLib.Math.Point P0 { get; set; } = new BeeLib.Math.Point(0,0);
        public BeeLib.Math.Point P1 { get; set; } = new BeeLib.Math.Point(0, 0);
        public BeeLib.Math.Point P2 { get; set; } = new BeeLib.Math.Point(0, 0);

        [JsonIgnore]
        private ICameraHandler _handler;

        public CameraJob(String strName, String strJobFile)
        {
            this.VisionType = JobType.TypeCamera;
            this.Name = strName;
            this.JobFile = strJobFile;
        }

        public CameraJob() : this(strDefaultCameraName, strDefaultJobFile) { }

        public override void Init()
        {
            base.Init();
            if (CamType != 0)
            {
                _handler = CreateHandler();
                _handler.Init();
            }
        }

        public bool IsIraypleLinescan() => CamType == 1 || CamType == 2;
        public bool IsItekAreaScan() => CamType == 5;

        private ICameraHandler CreateHandler()
        {
            switch (CamType)
            {
                case 1:
                case 2:
                    return new IRaypleLinescanHandler(this);
                case 5:
                    return new ItekAreaScanHandler(this);
                //case 4:
                //    return new WebcamHandler(this);
                default:
                    return new VppCameraHandler();
            }
        }

        protected override void ToolBlockRan(object sender, EventArgs e)
        {
            var tb = ToolBlock as CogToolBlock;

            LastValidImage = OutputImage;

            object toolBlockOutputImage = null;
            var hasOutputImage = tb.Outputs.Contains(strOutputImageKey);
            if (hasOutputImage)
                toolBlockOutputImage = tb.Outputs[strOutputImageKey].Value;

            if (CamType == 0)
            {
                if (hasOutputImage)
                    OutputImage = toolBlockOutputImage;
            }
            else
            {
                object capturedInputImage = null;
                if (tb.Inputs.Contains(strInputImageKey))
                    capturedInputImage = tb.Inputs[strInputImageKey].Value;

                OutputImage = toolBlockOutputImage ?? capturedInputImage;
            }

            Available = true;

            if (AllowFireEvent && (null != OnToolBlockRan)) OnToolBlockRan.Invoke(this, OutputImage, null);
            AllowFireEvent = true;
            RunStatus = tb.RunStatus.Result;
            if (CamType != 0 && OutputImage == null)
            {
                RunStatus = CogToolResultConstants.Error;
                Bug("[CameraJob] Camera output image is null after capture - Job:{0}, CamType:{1}", Name, CamType);
            }
            busyWait.Set();
        }

        public override bool RunTool()
        {
            var cameraToolBlock = ToolBlock as CogToolBlock;
            if (!TryGrabImage(cameraToolBlock))
            {
                return false;
            }

            if (!Available) busyWait.WaitOne(Timeout);
            if ((null != ToolBlock) && Available && Initialized)
            {
                var tb = ToolBlock as CogToolBlock;

                if (tb.Inputs.Contains(strParamsKey))
                    tb.Inputs[strParamsKey].Value = Params;

                //while (!Available) Task.Delay(1);
                busyWait.Reset();

                RunStatus = CogToolResultConstants.Error;
                Available = false;
                OutputImage = null;
                try
                {
                    if (!IsLightTrigger)
                    {
                        tb.Run();
                    }
                    else
                    {
                        JobController.RunLightJob(true, this);
                        tb.Run();
                        JobController.RunLightJob(false, this);
                    }
                }
                catch (Exception ex)
                {
                    //may cause violation error, take time then retry
                    Common.Bug("RunTool exception, Job {0}, message {1}", Name, ex.Message);
                    Common.Bug(ex.StackTrace);
                    Thread.Sleep(200);
                    tb.Run();
                }
                //MemoryCleanup();
                return Available;
            }
            return false;
        }

        AutoResetEvent busyWait = new AutoResetEvent(false);

        public override async Task<bool> RunToolAsync()
        {
            var cameraToolBlock = ToolBlock as CogToolBlock;
            if (!TryGrabImage(cameraToolBlock))
            {
                return false;
            }

            if (!Available) busyWait.WaitOne(Timeout);
            if ((null != ToolBlock) && Available && Initialized)
            {
                var tb = ToolBlock as CogToolBlock;

                if (tb.Inputs.Contains(strParamsKey))
                    tb.Inputs[strParamsKey].Value = Params;

                //while (!Available) await Task.Delay(1);
                busyWait.Reset();

                RunStatus = CogToolResultConstants.Error;
                Available = false;
                OutputImage = null;
                try
                {
                    //await Task.Run(tb.Run);
                    await Task.Run(() =>
                    {
                        lock (ToolBlockLock)
                        {
                            tb.Run();
                        }
                    });
                }
                catch (Exception ex)
                {
                    //may cause violation error, take time then retry
                    Common.Bug("RunTool exception, Job {0}, message {1}", Name, ex.Message);
                    Common.Bug(ex.StackTrace);
                    Thread.Sleep(200);
                    await Task.Run(() =>
                    {
                        lock (ToolBlockLock)
                        {
                            tb.Run();
                        }
                    });
                }
                return Available;
            }
            return false;
        }

        private bool TryGrabImage(CogToolBlock toolBlock)
        {
            if (CamType == 0)
            {
                return true;
            }

            if (_handler == null || !_handler.IsInitialized)
            {
                Bug("[CameraJob] Camera capture error - handler NOT initialized! CamType:{0}, Job:{1}", CamType, Name);
                RunStatus = CogToolResultConstants.Error;
                return false;
            }

            try
            {
                Info("[CameraJob] Camera capture - CamType:{0}, Job:{1}", CamType, Name);
                _handler.GrabImage(toolBlock);
                return true;
            }
            catch (Exception ex)
            {
                RunStatus = CogToolResultConstants.Error;
                OutputImage = null;
                Bug("[CameraJob] Camera capture error, Job:{0}, Message:{1}", Name, ex.Message);
                Bug(ex.StackTrace);
                return false;
            }
        }

        public void SetRuntimeConf(double exp, double gain)
        {
            _handler?.SetRuntimeConf(exp, gain);
        }

        public void CloseCamera()
        {
            _handler?.Close();
        }

        public override void Dispose()
        {
            _handler?.Close();
            _handler = null;
            base.Dispose();
        }

        ~CameraJob()
        {
            Dispose();
        }
    }
}
