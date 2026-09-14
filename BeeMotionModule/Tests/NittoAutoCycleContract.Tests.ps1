$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Assert-Equal
{
    param(
        $Actual,
        $Expected,
        [string]$Message
    )

    if ($Actual -ne $Expected)
    {
        throw "$Message Expected=$Expected Actual=$Actual"
    }
}

function Assert-Contains
{
    param(
        [string]$Text,
        [string]$Expected,
        [string]$Message
    )

    if (-not $Text.Contains($Expected))
    {
        throw $Message
    }
}

$projectPath = Join-Path $repositoryRoot 'BeeMotionModule\BeeMotionModule.csproj'
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild)
{
    throw 'MSBuild could not be located.'
}

& $msbuild $projectPath /p:Configuration=Debug /p:Platform=x64 /v:minimal | Out-Host
if ($LASTEXITCODE -ne 0)
{
    throw 'BeeMotionModule test build failed.'
}

$assemblyPath = Join-Path $repositoryRoot 'BeeMotionModule\bin\x64\Debug\BeeMotionModule.dll'
Add-Type -Path $assemblyPath

$config = New-Object BeeMotionModule.Models.MotionConfig
Assert-Equal $config.IO.TriggerBtnLeftDIBit 1 'X02 must map to zero-based DI1.'
Assert-Equal $config.IO.TriggerBtnRightDIBit 2 'X03 must map to zero-based DI2.'
Assert-Equal $config.IO.LoadCellLowDIBit 9 'X10 must map to zero-based DI9.'
Assert-Equal $config.IO.ForceReachedDIBit 10 'X11 Load Cell OK must map to zero-based DI10.'
Assert-Equal $config.IO.LoadCellHighDIBit 11 'X12 must map to zero-based DI11.'
Assert-Equal $config.IO.BrakeServoDOBit 0 'Y01 must map to zero-based DO0.'
Assert-Equal $config.IO.Button1LampDOBit 1 'Y02 must map to zero-based DO1.'
Assert-Equal $config.IO.Button2LampDOBit 2 'Y03 must map to zero-based DO2.'
Assert-Equal $config.IO.LoadCellResetDOBit 3 'Y04 must map to zero-based DO3.'
Assert-Equal $config.IO.LoadCellHoldDOBit 4 'Y05 must map to zero-based DO4.'
Assert-Equal $config.IO.Channel1LightTriggerDOBit 8 'Y09 must map to zero-based DO8.'
Assert-Equal $config.ClampJogVelocity 10.0 'Clamp jog speed must have a safe configurable default.'
Assert-Equal $config.ForceVisionOk $false 'Vision override must default OFF.'

$legacyConfig = New-Object BeeMotionModule.Models.MotionConfig
$legacyConfig.IO.TriggerBtnLeftDIBit = 0
$legacyConfig.IO.TriggerBtnRightDIBit = 1
$legacyConfig.IO.ForceReachedDIBit = 2
$legacyConfig.IO.SensorHomeUpDIBit = 3
$legacyConfig.IO.SensorDownLimitDIBit = 4
$legacyConfig.IO.SensorPartPresentDIBit = 5
$legacyConfig.IO.SystemStopDIBit = 6
[void]$legacyConfig.IO.MigrateTemporaryInputMapping()
Assert-Equal $legacyConfig.IO.TriggerBtnLeftDIBit 1 'Legacy X02 mapping must migrate to DI1.'
Assert-Equal $legacyConfig.IO.TriggerBtnRightDIBit 2 'Legacy X03 mapping must migrate to DI2.'
Assert-Equal $legacyConfig.IO.ForceReachedDIBit 10 'Legacy force input must migrate to DI10.'

$controller = Get-Content -LiteralPath (Join-Path $repositoryRoot 'BeeMotionModule\InovanceEcatController.cs') -Raw
$clampStart = $controller.IndexOf('public async Task<bool> ClampDownAsync')
$clampEnd = $controller.IndexOf('public async Task<bool> RetractUpAsync', $clampStart)
if ($clampStart -lt 0 -or $clampEnd -le $clampStart)
{
    throw 'Could not locate ClampDownAsync implementation.'
}
$clampMethod = $controller.Substring($clampStart, $clampEnd - $clampStart)
Assert-Contains $clampMethod 'LoadCellResetDOBit' 'Clamp must pulse the mapped LC_Reset output.'
Assert-Contains $clampMethod 'Task.Delay(200, ct)' 'LC_Reset pulse must remain ON for 200 ms.'
Assert-Contains $clampMethod 'WaitMoveDoneAsync' 'Clamp must finish the absolute positioning move before jog.'
Assert-Contains $clampMethod 'MoveJog(axis, -jogSpeed)' 'Clamp must jog in the negative direction.'
Assert-Contains $clampMethod 'IsForceTargetReached()' 'Clamp jog must stop on DI10 Load Cell OK.'
Assert-Contains $clampMethod 'finally' 'Clamp must stop the axis even when cancelled or faulted.'

$sequence = Get-Content -LiteralPath (Join-Path $repositoryRoot 'BeevisionSolution\Controller\MotionSequenceManager.cs') -Raw
Assert-Contains $sequence 'cfg.TwoHandSyncTimeMs' 'Two-hand start must enforce the configured simultaneous-press window.'
Assert-Contains $sequence 'twoHandTimer.ElapsedMilliseconds' 'A held button must not allow a late second button to start the cycle.'
Assert-Contains $sequence 'cfg.ForceVisionOk' 'Auto sequence must expose the approved Vision OK override.'
Assert-Contains $sequence 'Inspection completed; result overridden to OK by machine setting.' 'Inspection must run before its result is overridden.'

$motionView = Get-Content -LiteralPath (Join-Path $repositoryRoot 'BeevisionSolution\Views\MotionControlView.xaml') -Raw
Assert-Contains $motionView 'Force Vision OK (Test Only)' 'Motion settings must expose the Vision OK override.'
Assert-Contains $motionView 'Clamp Jog Speed (mm/s):' 'Motion settings must expose clamp jog speed.'

Write-Output 'Nitto auto-cycle contract checks passed.'
