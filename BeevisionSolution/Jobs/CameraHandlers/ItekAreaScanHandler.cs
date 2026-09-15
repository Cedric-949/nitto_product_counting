using Cognex.VisionPro;
using Cognex.VisionPro.ToolBlock;
using IKapBoardDotNet;
using IKapCDotNet;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static BeevisionSolution.Utils.Common;

namespace BeevisionSolution.Jobs.CameraHandlers
{
    public sealed class ItekAreaScanHandler : ICameraHandler
    {
        [DllImport("Kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr destination, IntPtr source, int length);

        private readonly Models.CameraJob _job;
        private readonly object _cameraLock = new object();
        private ITKDEVICE _device;
        private IntPtr _board = IntPtr.Zero;
        private bool _managerInitialized;
        private long _width;
        private long _height;
        private long _linePitch;
        private int _imageType;
        private int _bayerPattern;
        private int _bitDepth;

        public bool IsInitialized { get; private set; }

        public ItekAreaScanHandler(Models.CameraJob job)
        {
            _job = job;
        }

        public void Init()
        {
            lock (_cameraLock)
            {
                CloseCore();

                try
                {
                    CheckCamera(IKapC.ItkManInitialize(), "Initialize SDK");
                    _managerInitialized = true;

                    uint deviceIndex = FindDeviceIndex();
                    var deviceInfo = new ITKDEV_INFO();
                    CheckCamera(IKapC.ItkManGetDeviceInfo(deviceIndex, deviceInfo), "Read device information");
                    if (!string.Equals(deviceInfo.DeviceClass, "GigEVisionBoard", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Configured ITEK device is not a GigEVisionBoard camera.");
                    }

                    CheckCamera(
                        IKapC.ItkDevOpen(deviceIndex, IKapC.ITKDEV_VAL_ACCESS_MODE_EXCLUSIVE, ref _device),
                        "Open camera");

                    var boardInfo = new ITK_GVB_DEV_INFO();
                    CheckCamera(IKapC.ItkManGetGVBDeviceInfo(deviceIndex, boardInfo), "Read GigEVisionBoard information");
                    IntPtr boardInfoPointer = IKapC.get_itk_gvb_dev_info_IntPtr(boardInfo);
                    _board = IKapBoard.IKapOpenWithSpecificInfo(boardInfoPointer);
                    if (_board == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Open ITEK GigEVisionBoard failed.");
                    }

                    LoadBoardConfiguration();
                    ConfigureBoard();
                    ConfigureSoftwareTrigger();
                    ReadFrameGeometry();

                    IsInitialized = true;
                    _job.Available = true;
                    Info(
                        "[ITEK] Ready - Job:{0}, Device:{1}, Serial:{2}, Size:{3}x{4}, Pitch:{5}, BitDepth:{6}, Buffers:{7}",
                        _job.Name,
                        deviceInfo.ModelName,
                        deviceInfo.SerialNumber,
                        _width,
                        _height,
                        _linePitch,
                        _bitDepth,
                        _job.ItekBufferCount);
                }
                catch (Exception ex)
                {
                    Bug("[ITEK] Init failed - Job:{0}, Error:{1}", _job.Name, ex.Message);
                    _job.Available = false;
                    CloseCore();
                }
            }
        }

        public void GrabImage(CogToolBlock toolBlock)
        {
            if (toolBlock == null || !toolBlock.Inputs.Contains("InputImage"))
            {
                throw new InvalidOperationException("Camera tool block must contain InputImage.");
            }

            lock (_cameraLock)
            {
                if (!IsInitialized || _device == null || _board == IntPtr.Zero)
                {
                    throw new InvalidOperationException("ITEK camera is not initialized.");
                }

                ICogImage image = null;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    CheckCamera(
                        IKapC.ItkDevExecuteCommand(_device, "AcquisitionStop"),
                        "Stop camera acquisition before grab");
                    CheckBoard(IKapBoard.IKapStopGrab(_board), "Stop pending board grab");
                    CheckBoard(IKapBoard.IKapStartGrab(_board, 1), "Start single-frame grab");
                    CheckCamera(
                        IKapC.ItkDevExecuteCommand(_device, "AcquisitionStart"),
                        "Start camera acquisition");
                    CheckCamera(IKapC.ItkDevExecuteCommand(_device, "TriggerSoftware"), "Send software trigger");
                    CheckBoard(IKapBoard.IKapWaitGrab(_board), "Wait for frame");

                    IntPtr framePointer = IntPtr.Zero;
                    CheckBoard(IKapBoard.IKapGetBufferAddress(_board, 0, ref framePointer), "Get frame buffer");
                    if (framePointer == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("ITEK returned an empty frame buffer.");
                    }

                    image = ConvertFrame(framePointer);
                    toolBlock.Inputs["InputImage"].Value = image;
                    image = null;

                    stopwatch.Stop();
                    Info("[ITEK] Grab complete - Job:{0}, Time:{1}ms", _job.Name, stopwatch.ElapsedMilliseconds);
                }
                finally
                {
                    if (_device != null)
                    {
                        try { IKapC.ItkDevExecuteCommand(_device, "AcquisitionStop"); } catch { }
                    }

                    if (_board != IntPtr.Zero)
                    {
                        try { IKapBoard.IKapStopGrab(_board); } catch { }
                    }

                    (image as IDisposable)?.Dispose();
                }
            }
        }

        public void SetRuntimeConf(double exposure, double gain)
        {
            lock (_cameraLock)
            {
                if (!IsInitialized)
                {
                    return;
                }

                if (exposure > 0)
                {
                    CheckCamera(IKapC.ItkDevSetDouble(_device, "ExposureTime", exposure), "Set exposure");
                }

                if (gain >= 0)
                {
                    uint status = IKapC.ItkDevSetDouble(_device, "DigitalGain", gain);
                    if (status != IKapC.ITKSTATUS_OK)
                    {
                        CheckCamera(IKapC.ItkDevSetDouble(_device, "Gain", gain), "Set gain");
                    }
                }
            }
        }

        public void Close()
        {
            lock (_cameraLock)
            {
                CloseCore();
            }
        }

        private uint FindDeviceIndex()
        {
            uint deviceCount = 0;
            CheckCamera(IKapC.ItkManGetDeviceCount(ref deviceCount), "Enumerate cameras");
            if (deviceCount == 0)
            {
                throw new InvalidOperationException("No ITEK camera was found.");
            }

            if (string.IsNullOrWhiteSpace(_job.ItekSerialNumber))
            {
                if (_job.ItekDeviceIndex >= deviceCount)
                {
                    throw new InvalidOperationException("ITEK device index is outside the enumerated camera list.");
                }

                return _job.ItekDeviceIndex;
            }

            for (uint index = 0; index < deviceCount; index++)
            {
                var info = new ITKDEV_INFO();
                if (IKapC.ItkManGetDeviceInfo(index, info) == IKapC.ITKSTATUS_OK &&
                    string.Equals(info.SerialNumber, _job.ItekSerialNumber, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            throw new InvalidOperationException("Configured ITEK camera serial number was not found.");
        }

        private void LoadBoardConfiguration()
        {
            if (string.IsNullOrWhiteSpace(_job.ItekBoardConfigPath))
            {
                Info("[ITEK] Board config path is empty; current camera and board settings will be used.");
                return;
            }

            string path = ResolveProfilePath(_job.ItekBoardConfigPath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("ITEK board config file was not found.", path);
            }

            CheckBoard(IKapBoard.IKapLoadConfigurationFromFile(_board, path), "Load board configuration");
        }

        private static string ResolveProfilePath(string path)
        {
            if (Path.IsPathRooted(path))
            {
                return path;
            }

            string profilePath = Path.Combine(Utils.Common.ProfileFolder, path);
            return File.Exists(profilePath)
                ? profilePath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }

        private void ConfigureBoard()
        {
            int bufferCount = Math.Max(1, Math.Min(4, _job.ItekBufferCount));
            CheckBoard(IKapBoard.IKapSetInfo(_board, (uint)IKapBoard.IKP_FRAME_COUNT, bufferCount), "Set frame buffer count");
            CheckBoard(IKapBoard.IKapSetInfo(_board, (uint)IKapBoard.IKP_TIME_OUT, Math.Max(1, _job.ItekGrabTimeoutMs)), "Set grab timeout");
            CheckBoard(IKapBoard.IKapSetInfo(_board, (uint)IKapBoard.IKP_GRAB_MODE, IKapBoard.IKP_GRAB_NON_BLOCK), "Set non-blocking grab");
            CheckBoard(
                IKapBoard.IKapSetInfo(
                    _board,
                    (uint)IKapBoard.IKP_FRAME_TRANSFER_MODE,
                    IKapBoard.IKP_FRAME_TRANSFER_SYNCHRONOUS_NEXT_EMPTY_WITH_PROTECT),
                "Set protected transfer mode");
        }

        private void ConfigureSoftwareTrigger()
        {
            TrySetCameraEnum("TriggerSelector", "FrameStart", "Set trigger selector");
            CheckCamera(IKapC.ItkDevFromString(_device, "TriggerMode", "On"), "Enable trigger mode");
            CheckCamera(IKapC.ItkDevFromString(_device, "TriggerSource", "Software"), "Set software trigger source");
            TrySetCameraEnum("TriggerActivation", "RisingEdge", "Set trigger activation");
        }

        private void TrySetCameraEnum(string featureName, string value, string operation)
        {
            uint status = IKapC.ItkDevFromString(_device, featureName, value);
            if (status == IKapC.ITKSTATUS_OK)
            {
                return;
            }

            Info("[ITEK] Optional camera setting skipped - {0}: {1}={2}, Status: 0x{3:X8}", operation, featureName, value, status);
        }

        private void ReadFrameGeometry()
        {
            long frameSize = 0;
            CheckCamera(IKapC.ItkDevGetInt64(_device, "Width", ref _width), "Read image width");
            CheckCamera(IKapC.ItkDevGetInt64(_device, "Height", ref _height), "Read image height");
            CheckBoard(IKapBoard.IKapGetInfo64(_board, (uint)IKapBoard.IKP_FRAME_SIZE, ref frameSize), "Read frame size");
            CheckBoard(IKapBoard.IKapGetInfo(_board, (uint)IKapBoard.IKP_IMAGE_TYPE, ref _imageType), "Read image type");
            CheckBoard(IKapBoard.IKapGetInfo(_board, (uint)IKapBoard.IKP_BAYER_PATTERN, ref _bayerPattern), "Read Bayer pattern");
            CheckBoard(IKapBoard.IKapGetInfo(_board, (uint)IKapBoard.IKP_DATA_FORMAT, ref _bitDepth), "Read bit depth");

            if (_width <= 0 || _height <= 0 || _width > int.MaxValue || _height > int.MaxValue || frameSize <= 0)
            {
                throw new InvalidOperationException("ITEK returned invalid frame geometry.");
            }

            _linePitch = frameSize / _height;
            if (_linePitch <= 0 || _linePitch > int.MaxValue)
            {
                throw new InvalidOperationException("ITEK returned an invalid line pitch.");
            }
        }

        private ICogImage ConvertFrame(IntPtr source)
        {
            int width = (int)_width;
            int height = (int)_height;
            int pitch = (int)_linePitch;

            if (_imageType == IKapBoard.IKP_IMAGE_TYPE_VAL_MONOCHROME)
            {
                return CopyMono(source, width, height, pitch, _bitDepth);
            }

            if (_imageType == IKapBoard.IKP_IMAGE_TYPE_VAL_COLORFUL)
            {
                return DemosaicBayer(source, width, height, pitch, _bitDepth, _bayerPattern);
            }

            if (_imageType == IKapBoard.IKP_IMAGE_TYPE_VAL_RGB ||
                _imageType == IKapBoard.IKP_IMAGE_TYPE_VAL_RGBC ||
                _imageType == IKapBoard.IKP_IMAGE_TYPE_VAL_BGR ||
                _imageType == IKapBoard.IKP_IMAGE_TYPE_VAL_BGRC)
            {
                return CopyInterleavedColor(source, width, height, pitch, _bitDepth, _imageType);
            }

            throw new NotSupportedException("ITEK image type is not supported: " + _imageType);
        }

        private static CogImage8Grey CopyMono(IntPtr source, int width, int height, int sourceStride, int bitDepth)
        {
            var image = new CogImage8Grey(width, height);
            ICogImage8PixelMemory memory = image.Get8GreyPixelMemory(CogImageDataModeConstants.Write, 0, 0, width, height);

            try
            {
                if (bitDepth <= 8)
                {
                    Parallel.For(0, height, row =>
                        CopyMemory(IntPtr.Add(memory.Scan0, row * memory.Stride), IntPtr.Add(source, row * sourceStride), width));
                }
                else
                {
                    int shift = bitDepth - 8;
                    Parallel.For(0, height, row => CopyMono16Row(source, memory.Scan0, row, width, sourceStride, memory.Stride, shift));
                }
            }
            catch
            {
                image.Dispose();
                throw;
            }
            finally
            {
                memory.Dispose();
            }

            return image;
        }

        private static unsafe void CopyMono16Row(
            IntPtr source,
            IntPtr destination,
            int row,
            int width,
            int sourceStride,
            int destinationStride,
            int shift)
        {
            ushort* sourceRow = (ushort*)((byte*)source + row * sourceStride);
            byte* destinationRow = (byte*)destination + row * destinationStride;
            for (int column = 0; column < width; column++)
            {
                destinationRow[column] = (byte)(sourceRow[column] >> shift);
            }
        }

        private static CogImage24PlanarColor DemosaicBayer(
            IntPtr source,
            int width,
            int height,
            int sourceStride,
            int bitDepth,
            int bayerPattern)
        {
            var image = new CogImage24PlanarColor(width, height);
            ICogImage8PixelMemory redMemory = null;
            ICogImage8PixelMemory greenMemory = null;
            ICogImage8PixelMemory blueMemory = null;
            image.Get24PlanarColorPixelMemory(
                CogImageDataModeConstants.Write,
                0,
                0,
                width,
                height,
                out redMemory,
                out greenMemory,
                out blueMemory);

            try
            {
                int redRow = 0;
                int redColumn = 0;
                int blueRow = 1;
                int blueColumn = 1;
                if (bayerPattern == IKapBoard.IKP_BAYER_PATTERN_VAL_BGGR)
                {
                    redRow = 1;
                    redColumn = 1;
                    blueRow = 0;
                    blueColumn = 0;
                }
                else if (bayerPattern == IKapBoard.IKP_BAYER_PATTERN_VAL_GRBG)
                {
                    redColumn = 1;
                    blueColumn = 0;
                }
                else if (bayerPattern == IKapBoard.IKP_BAYER_PATTERN_VAL_GBRG)
                {
                    redRow = 1;
                    blueRow = 0;
                }

                int shift = Math.Max(0, bitDepth - 8);
                bool is16Bit = bitDepth > 8;
                Parallel.For(
                    0,
                    height,
                    row => DemosaicRow(
                        source,
                        redMemory.Scan0,
                        greenMemory.Scan0,
                        blueMemory.Scan0,
                        row,
                        width,
                        height,
                        sourceStride,
                        redMemory.Stride,
                        greenMemory.Stride,
                        blueMemory.Stride,
                        is16Bit,
                        shift,
                        redRow,
                        redColumn,
                        blueRow,
                        blueColumn));
            }
            catch
            {
                image.Dispose();
                throw;
            }
            finally
            {
                redMemory?.Dispose();
                greenMemory?.Dispose();
                blueMemory?.Dispose();
            }

            return image;
        }

        private static unsafe void DemosaicRow(
            IntPtr source,
            IntPtr red,
            IntPtr green,
            IntPtr blue,
            int row,
            int width,
            int height,
            int sourceStride,
            int redStride,
            int greenStride,
            int blueStride,
            bool is16Bit,
            int shift,
            int redRow,
            int redColumn,
            int blueRow,
            int blueColumn)
        {
            byte* redDestination = (byte*)red + row * redStride;
            byte* greenDestination = (byte*)green + row * greenStride;
            byte* blueDestination = (byte*)blue + row * blueStride;
            int previousRow = row == 0 ? 0 : row - 1;
            int nextRow = row == height - 1 ? height - 1 : row + 1;
            bool redPatternRow = (row & 1) == redRow;
            bool bluePatternRow = (row & 1) == blueRow;

            for (int column = 0; column < width; column++)
            {
                int previousColumn = column == 0 ? 0 : column - 1;
                int nextColumn = column == width - 1 ? width - 1 : column + 1;
                bool isRed = redPatternRow && (column & 1) == redColumn;
                bool isBlue = bluePatternRow && (column & 1) == blueColumn;
                byte center = ReadBayer(source, row, column, sourceStride, is16Bit, shift);
                byte redValue;
                byte greenValue;
                byte blueValue;

                if (isRed || isBlue)
                {
                    greenValue = Average4(
                        ReadBayer(source, previousRow, column, sourceStride, is16Bit, shift),
                        ReadBayer(source, nextRow, column, sourceStride, is16Bit, shift),
                        ReadBayer(source, row, previousColumn, sourceStride, is16Bit, shift),
                        ReadBayer(source, row, nextColumn, sourceStride, is16Bit, shift));
                    byte diagonal = Average4(
                        ReadBayer(source, previousRow, previousColumn, sourceStride, is16Bit, shift),
                        ReadBayer(source, previousRow, nextColumn, sourceStride, is16Bit, shift),
                        ReadBayer(source, nextRow, previousColumn, sourceStride, is16Bit, shift),
                        ReadBayer(source, nextRow, nextColumn, sourceStride, is16Bit, shift));
                    redValue = isRed ? center : diagonal;
                    blueValue = isBlue ? center : diagonal;
                }
                else
                {
                    greenValue = center;
                    bool greenOnRedRow = redPatternRow;
                    byte horizontal = Average2(
                        ReadBayer(source, row, previousColumn, sourceStride, is16Bit, shift),
                        ReadBayer(source, row, nextColumn, sourceStride, is16Bit, shift));
                    byte vertical = Average2(
                        ReadBayer(source, previousRow, column, sourceStride, is16Bit, shift),
                        ReadBayer(source, nextRow, column, sourceStride, is16Bit, shift));
                    redValue = greenOnRedRow ? horizontal : vertical;
                    blueValue = greenOnRedRow ? vertical : horizontal;
                }

                redDestination[column] = redValue;
                greenDestination[column] = greenValue;
                blueDestination[column] = blueValue;
            }
        }

        private static unsafe byte ReadBayer(
            IntPtr source,
            int row,
            int column,
            int sourceStride,
            bool is16Bit,
            int shift)
        {
            byte* rowPointer = (byte*)source + row * sourceStride;
            return is16Bit
                ? (byte)(((ushort*)rowPointer)[column] >> shift)
                : rowPointer[column];
        }

        private static byte Average2(byte first, byte second)
        {
            return (byte)((first + second) >> 1);
        }

        private static byte Average4(byte first, byte second, byte third, byte fourth)
        {
            return (byte)((first + second + third + fourth) >> 2);
        }

        private static CogImage24PlanarColor CopyInterleavedColor(
            IntPtr source,
            int width,
            int height,
            int sourceStride,
            int bitDepth,
            int imageType)
        {
            var image = new CogImage24PlanarColor(width, height);
            ICogImage8PixelMemory redMemory = null;
            ICogImage8PixelMemory greenMemory = null;
            ICogImage8PixelMemory blueMemory = null;
            image.Get24PlanarColorPixelMemory(
                CogImageDataModeConstants.Write,
                0,
                0,
                width,
                height,
                out redMemory,
                out greenMemory,
                out blueMemory);

            try
            {
                bool rgb = imageType == IKapBoard.IKP_IMAGE_TYPE_VAL_RGB || imageType == IKapBoard.IKP_IMAGE_TYPE_VAL_RGBC;
                int channels = imageType == IKapBoard.IKP_IMAGE_TYPE_VAL_RGBC || imageType == IKapBoard.IKP_IMAGE_TYPE_VAL_BGRC ? 4 : 3;
                bool is16Bit = bitDepth > 8;
                int shift = Math.Max(0, bitDepth - 8);
                Parallel.For(
                    0,
                    height,
                    row => CopyColorRow(
                        source,
                        redMemory.Scan0,
                        greenMemory.Scan0,
                        blueMemory.Scan0,
                        row,
                        width,
                        sourceStride,
                        redMemory.Stride,
                        greenMemory.Stride,
                        blueMemory.Stride,
                        rgb,
                        channels,
                        is16Bit,
                        shift));
            }
            catch
            {
                image.Dispose();
                throw;
            }
            finally
            {
                redMemory?.Dispose();
                greenMemory?.Dispose();
                blueMemory?.Dispose();
            }

            return image;
        }

        private static unsafe void CopyColorRow(
            IntPtr source,
            IntPtr red,
            IntPtr green,
            IntPtr blue,
            int row,
            int width,
            int sourceStride,
            int redStride,
            int greenStride,
            int blueStride,
            bool rgb,
            int channels,
            bool is16Bit,
            int shift)
        {
            byte* sourceRow = (byte*)source + row * sourceStride;
            byte* redDestination = (byte*)red + row * redStride;
            byte* greenDestination = (byte*)green + row * greenStride;
            byte* blueDestination = (byte*)blue + row * blueStride;

            for (int column = 0; column < width; column++)
            {
                int offset = column * channels;
                byte first;
                byte second;
                byte third;
                if (is16Bit)
                {
                    ushort* pixel = (ushort*)sourceRow + offset;
                    first = (byte)(pixel[0] >> shift);
                    second = (byte)(pixel[1] >> shift);
                    third = (byte)(pixel[2] >> shift);
                }
                else
                {
                    first = sourceRow[offset];
                    second = sourceRow[offset + 1];
                    third = sourceRow[offset + 2];
                }

                redDestination[column] = rgb ? first : third;
                greenDestination[column] = second;
                blueDestination[column] = rgb ? third : first;
            }
        }

        private void CloseCore()
        {
            IsInitialized = false;

            if (_device != null)
            {
                try { IKapC.ItkDevExecuteCommand(_device, "AcquisitionStop"); } catch { }
            }

            if (_board != IntPtr.Zero)
            {
                try { IKapBoard.IKapStopGrab(_board); } catch { }
                try { IKapBoard.IKapClose(_board); } catch { }
                _board = IntPtr.Zero;
            }

            if (_device != null)
            {
                try { IKapC.ItkDevClose(_device); } catch { }
                _device = null;
            }

            if (_managerInitialized)
            {
                try { IKapC.ItkManTerminate(); } catch { }
                _managerInitialized = false;
            }
        }

        private static void CheckCamera(uint status, string operation)
        {
            if (status != IKapC.ITKSTATUS_OK)
            {
                throw new InvalidOperationException(operation + " failed. ITEK status: 0x" + status.ToString("X8"));
            }
        }

        private static void CheckBoard(int status, string operation)
        {
            if (status != IKapBoard.IK_RTN_OK)
            {
                try
                {
                    var errorInfo = new IKAPERRORINFO();
                    IKapBoard.IKapGetLastError(errorInfo, true);
                    Bug(
                        "[ITEK] Board error - Operation:{0}, Status:{1}, ErrorCode:0x{2:X8}, BoardType:{3}, BoardIndex:{4}",
                        operation,
                        status,
                        errorInfo.uErrorCode,
                        errorInfo.uBoardType,
                        errorInfo.uBoardIndex);
                }
                catch (Exception errorException)
                {
                    Bug("[ITEK] Board error details unavailable - Operation:{0}, Detail:{1}", operation, errorException.Message);
                }
                throw new InvalidOperationException(operation + " failed. ITEK board status: " + status);
            }
        }
    }
}
