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
    $cameraJobType = $assembly.GetType("BeevisionSolution.Models.CameraJob", $true)
    $handlerContract = $assembly.GetType("BeevisionSolution.Jobs.CameraHandlers.ICameraHandler", $true)
    $privateInstance = [Reflection.BindingFlags]([Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic)
    $disposeMethod = $cameraJobType.GetMethod("Dispose")
    if ($disposeMethod.DeclaringType -ne $cameraJobType)
    {
        throw "CameraJob must own disposal of its external camera handler."
    }

    $handlerType = $assembly.GetType("BeevisionSolution.Jobs.CameraHandlers.ItekAreaScanHandler", $true)
    $handlerSource = Get-Content -LiteralPath (Join-Path $root "BeevisionSolution\Jobs\CameraHandlers\ItekAreaScanHandler.cs") -Raw

    if (-not $handlerContract.IsAssignableFrom($handlerType))
    {
        throw "ItekAreaScanHandler must implement ICameraHandler."
    }

    if ($handlerSource -notmatch 'IKapStartGrab\(_board,\s*0\)')
    {
        throw "ItekAreaScanHandler must start the ITEK board in continuous grab mode before software trigger."
    }

    if ($handlerSource -match 'IKapStartGrab\(_board,\s*1\)')
    {
        throw "ItekAreaScanHandler must not restart the ITEK board in single-frame grab mode for each software trigger."
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

    Write-Output "ITEK AREA SCAN CONTRACT VERIFIED"
}
finally
{
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}
