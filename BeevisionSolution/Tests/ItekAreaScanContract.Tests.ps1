param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bin = Join-Path $root "BeevisionSolution\bin\x64\$Configuration"
$appPath = Join-Path $bin "BeevisionSolution.exe"

if (-not (Test-Path -LiteralPath $appPath))
{
    throw "Build output not found: $appPath"
}

$resolver = [ResolveEventHandler] {
    param($sender, $eventArgs)

    try
    {
        $assemblyName = New-Object Reflection.AssemblyName($eventArgs.Name)
        $candidate = Join-Path $bin ($assemblyName.Name + ".dll")
        if (Test-Path -LiteralPath $candidate)
        {
            return [Reflection.Assembly]::LoadFrom($candidate)
        }
    }
    catch { }

    return $null
}

[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

try
{
    $assembly = [Reflection.Assembly]::LoadFrom($appPath)
    foreach ($reference in $assembly.GetReferencedAssemblies() | Where-Object { $_.Name -like "Cognex.VisionPro*" })
    {
        try
        {
            [Reflection.Assembly]::Load($reference) | Out-Null
        }
        catch { }
    }

    $cameraJobType = $assembly.GetType("BeevisionSolution.Models.CameraJob", $true)
    $handlerContract = $assembly.GetType("BeevisionSolution.Jobs.CameraHandlers.ICameraHandler", $true)
    $privateInstance = [Reflection.BindingFlags]([Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic)
    $disposeMethod = $cameraJobType.GetMethod("Dispose")
    if ($disposeMethod.DeclaringType -ne $cameraJobType)
    {
        throw "CameraJob must own disposal of its external camera handler."
    }

    $handlerType = $assembly.GetType("BeevisionSolution.Jobs.CameraHandlers.ItekAreaScanHandler", $true)
    $toolBlockType = [AppDomain]::CurrentDomain.GetAssemblies() |
        ForEach-Object { $_.GetType("Cognex.VisionPro.ToolBlock.CogToolBlock", $false) } |
        Where-Object { $null -ne $_ } |
        Select-Object -First 1
    $terminalType = [AppDomain]::CurrentDomain.GetAssemblies() |
        ForEach-Object { $_.GetType("Cognex.VisionPro.ToolBlock.CogToolBlockTerminal", $false) } |
        Where-Object { $null -ne $_ } |
        Select-Object -First 1
    $imageType = [AppDomain]::CurrentDomain.GetAssemblies() |
        ForEach-Object { $_.GetType("Cognex.VisionPro.CogImage8Grey", $false) } |
        Where-Object { $null -ne $_ } |
        Select-Object -First 1
    $toolResultType = [AppDomain]::CurrentDomain.GetAssemblies() |
        ForEach-Object { $_.GetType("Cognex.VisionPro.CogToolResultConstants", $false) } |
        Where-Object { $null -ne $_ } |
        Select-Object -First 1

    if ($toolBlockType -eq $null -or $terminalType -eq $null -or $imageType -eq $null -or $toolResultType -eq $null)
    {
        throw "VisionPro runtime types must be available for CameraJob behavior contracts."
    }

    $handlerSource = Get-Content -LiteralPath (Join-Path $root "BeevisionSolution\Jobs\CameraHandlers\ItekAreaScanHandler.cs") -Raw
    $cameraJobSource = Get-Content -LiteralPath (Join-Path $root "BeevisionSolution\Jobs\CameraJob.cs") -Raw
    $toolSettingSource = Get-Content -LiteralPath (Join-Path $root "BeevisionSolution\Views\ToolSettingView.xaml.cs") -Raw
    $toolSettingMarkup = Get-Content -LiteralPath (Join-Path $root "BeevisionSolution\Views\ToolSettingView.xaml") -Raw
    $motionControlSource = Get-Content -LiteralPath (Join-Path $root "BeevisionSolution\Views\MotionControlView.xaml.cs") -Raw
    $motionControlMarkup = Get-Content -LiteralPath (Join-Path $root "BeevisionSolution\Views\MotionControlView.xaml") -Raw

    if (-not $handlerContract.IsAssignableFrom($handlerType))
    {
        throw "ItekAreaScanHandler must implement ICameraHandler."
    }

    if ($handlerSource -notmatch 'IKapStartGrab\(_board,\s*1\)')
    {
        throw "ItekAreaScanHandler must start a single-frame board grab for each software trigger."
    }

    if ($handlerSource -match 'IKapStartGrab\(_board,\s*0\)')
    {
        throw "ItekAreaScanHandler must not leave the board in continuous grab mode."
    }

    if ($handlerSource -match 'IKapClearGrab\(_board\)')
    {
        throw "ItekAreaScanHandler must rely on IKapStartGrab to clear stale frame data."
    }

    if ($handlerSource -match 'IKapWaitOneFrameReady')
    {
        throw "ItekAreaScanHandler must use IKapWaitGrab for single-frame acquisition."
    }

    if ($handlerSource -notmatch 'IKapWaitGrab\(_board\)')
    {
        throw "ItekAreaScanHandler must wait for the single-frame grab to complete."
    }

    if ($handlerSource -notmatch 'IKapGetBufferAddress\(_board,\s*0\s*,')
    {
        throw "ItekAreaScanHandler must read the single-frame buffer at index 0."
    }

    if ($handlerSource -notmatch 'IKapGetLastError')
    {
        throw "ItekAreaScanHandler must log the ITEK board last error when a board call fails."
    }

    $grabStart = $handlerSource.IndexOf("public void GrabImage")
    $runtimeConfigStart = $handlerSource.IndexOf("public void SetRuntimeConf")
    $grabSource = $handlerSource.Substring($grabStart, $runtimeConfigStart - $grabStart)
    $sequence = @(
        "AcquisitionStop",
        "IKapStopGrab",
        "IKapStartGrab(_board, 1)",
        "AcquisitionStart",
        "TriggerSoftware",
        "IKapWaitGrab(_board)"
    )
    $lastIndex = -1
    foreach ($operation in $sequence)
    {
        $index = $grabSource.IndexOf($operation)
        if ($index -lt 0 -or $index -lt $lastIndex)
        {
            throw "ItekAreaScanHandler must execute single-frame operations in stop, arm, trigger, wait order."
        }

        $lastIndex = $index
    }

    $finallyIndex = $grabSource.LastIndexOf("finally")
    if ($finallyIndex -lt 0 -or
        $grabSource.IndexOf("AcquisitionStop", $finallyIndex) -lt 0 -or
        $grabSource.IndexOf("IKapStopGrab", $finallyIndex) -lt 0)
    {
        throw "ItekAreaScanHandler must stop camera acquisition and board grab in finally."
    }

    if ($handlerSource -match 'StartContinuousGrab|EnsureContinuousGrab|PrepareFrameForTrigger|_boardGrabbing')
    {
        throw "ItekAreaScanHandler must not retain continuous-grab state or helpers."
    }

    if ($cameraJobSource -notmatch 'Capture failed|capture failed|Camera capture error')
    {
        throw "CameraJob must handle camera capture failures before running the ToolBlock."
    }

    $tryGrabStart = $cameraJobSource.IndexOf("private bool TryGrabImage")
    $setRuntimeStart = $cameraJobSource.IndexOf("public void SetRuntimeConf")
    $tryGrabSource = $cameraJobSource.Substring($tryGrabStart, $setRuntimeStart - $tryGrabStart)
    if ($tryGrabSource -notmatch 'RunStatus\s*=\s*CogToolResultConstants\.Error' -or
        $tryGrabSource -notmatch 'OutputImage\s*=\s*null' -or
        $tryGrabSource -notmatch 'return false')
    {
        throw "CameraJob must clear the output image and fail the job when capture fails."
    }

    foreach ($runMethod in @("public override bool RunTool()", "public override async Task<bool> RunToolAsync()"))
    {
        $runStart = $cameraJobSource.IndexOf($runMethod)
        $nextMethod = $cameraJobSource.IndexOf("public ", $runStart + $runMethod.Length)
        $runSource = $cameraJobSource.Substring($runStart, $nextMethod - $runStart)
        if ($runSource.IndexOf("TryGrabImage") -lt 0 -or
            $runSource.IndexOf("TryGrabImage") -gt $runSource.IndexOf("tb.Run"))
        {
            throw "CameraJob must capture successfully before running the ToolBlock."
        }
    }

    if ($toolSettingSource -match 'Dispatcher\.BeginInvoke')
    {
        throw "ToolSettingView must not schedule a duplicate initial job load."
    }

    if ($toolSettingMarkup -match 'SelectedIndex\s*=\s*"0"')
    {
        throw "ToolSettingView must not initialize the first selection in both XAML and code."
    }

    if ($motionControlMarkup -notmatch 'x:Name="btnTriggerCam"[^>]*Click="BtnTriggerCam_Click"')
    {
        throw "MotionControlView must expose a manual camera trigger button."
    }

    if ($motionControlSource -notmatch 'BtnTriggerCam_Click[\s\S]*JobController\.RunJobByIdAsync\(0\)')
    {
        throw "Manual camera trigger must use the existing inspection job pipeline."
    }

    if ($motionControlSource -notmatch '_isManualCameraTriggerRunning')
    {
        throw "Manual camera trigger must prevent overlapping inspection runs."
    }

    $job = [Activator]::CreateInstance($cameraJobType)
    $job.CamType = 5

    $createHandler = $cameraJobType.GetMethod(
        "CreateHandler",
        $privateInstance)
    $handler = $createHandler.Invoke($job, $null)

    if ($handler.GetType() -ne $handlerType)
    {
        throw "CamType 5 must create ItekAreaScanHandler."
    }

    $expectedDefaults = @{
        ItekDeviceIndex = 0
        ItekSerialNumber = ""
        ItekGrabTimeoutMs = 5000
        ItekBufferCount = 2
        ItekBoardConfigPath = ""
    }

    foreach ($entry in $expectedDefaults.GetEnumerator())
    {
        $actual = $cameraJobType.GetProperty($entry.Key).GetValue($job, $null)
        if ($actual -ne $entry.Value)
        {
            throw "$($entry.Key) default must be '$($entry.Value)', actual '$actual'."
        }
    }

    $toolBlockRan = $cameraJobType.GetMethod("ToolBlockRan", $privateInstance)
    $toolBlockProperty = $cameraJobType.GetProperty("ToolBlock")
    $outputImageProperty = $cameraJobType.GetProperty("OutputImage")
    $runStatusProperty = $cameraJobType.GetProperty("RunStatus")

    function New-TestImage
    {
        [Activator]::CreateInstance($imageType, @([int]2, [int]2))
    }

    function New-TestToolBlock
    {
        param(
            [object]$InputImage,
            [object]$OutputImage,
            [bool]$IncludeOutputTerminal = $true
        )

        $tb = [Activator]::CreateInstance($toolBlockType)
        $inputTerminal = [Activator]::CreateInstance($terminalType, @("InputImage", $imageType))
        $inputTerminal.Value = $InputImage
        $tb.Inputs.Add($inputTerminal)

        if ($IncludeOutputTerminal)
        {
            $outputTerminal = [Activator]::CreateInstance($terminalType, @("OutputImage", $imageType))
            $outputTerminal.Value = $OutputImage
            $tb.Outputs.Add($outputTerminal)
        }

        return $tb
    }

    function Invoke-CameraToolBlockRan
    {
        param(
            [int]$CamType,
            [object]$InputImage,
            [object]$OutputImage,
            [bool]$IncludeOutputTerminal = $true
        )

        $cameraJob = [Activator]::CreateInstance($cameraJobType)
        $cameraJob.CamType = $CamType
        $tb = New-TestToolBlock -InputImage $InputImage -OutputImage $OutputImage -IncludeOutputTerminal $IncludeOutputTerminal
        $toolBlockProperty.SetValue($cameraJob, $tb, $null)
        $toolBlockRan.Invoke($cameraJob, @($tb, [EventArgs]::Empty))

        return @{
            Job = $cameraJob
            ToolBlock = $tb
            OutputImage = $outputImageProperty.GetValue($cameraJob, $null)
            RunStatus = $runStatusProperty.GetValue($cameraJob, $null)
        }
    }

    $capturedImage = New-TestImage
    $result = Invoke-CameraToolBlockRan -CamType 5 -InputImage $capturedImage -OutputImage $null
    if (-not [object]::ReferenceEquals($capturedImage, $result.OutputImage))
    {
        throw "External CameraJob must pass captured InputImage through when VPP OutputImage is null."
    }

    $processedImage = New-TestImage
    $result = Invoke-CameraToolBlockRan -CamType 5 -InputImage $capturedImage -OutputImage $processedImage
    if (-not [object]::ReferenceEquals($processedImage, $result.OutputImage))
    {
        throw "External CameraJob must keep non-null VPP OutputImage ahead of captured InputImage."
    }

    $result = Invoke-CameraToolBlockRan -CamType 5 -InputImage $null -OutputImage $null
    if ($result.RunStatus -ne [Enum]::Parse($toolResultType, "Error"))
    {
        throw "External CameraJob must report Error when neither OutputImage nor captured InputImage is available."
    }

    $result = Invoke-CameraToolBlockRan -CamType 0 -InputImage $capturedImage -OutputImage $null
    if ($result.OutputImage -ne $null)
    {
        throw "VPP CameraJob must preserve existing OutputImage terminal behavior for CamType 0."
    }

    Write-Output "ITEK AREA SCAN CONTRACT VERIFIED"
}
finally
{
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}
