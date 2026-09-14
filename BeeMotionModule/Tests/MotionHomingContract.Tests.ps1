$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

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

function Assert-NotContains
{
    param(
        [string]$Text,
        [string]$Unexpected,
        [string]$Message
    )

    if ($Text.Contains($Unexpected))
    {
        throw $Message
    }
}

$controllerPath = Join-Path $repositoryRoot 'BeeMotionModule\InovanceEcatController.cs'
$controller = Get-Content -LiteralPath $controllerPath -Raw
$homeStart = $controller.IndexOf('public async Task<bool> HomeAsync')
$homeEnd = $controller.IndexOf('public bool MoveJog', $homeStart)
if ($homeStart -lt 0 -or $homeEnd -le $homeStart)
{
    throw 'Could not locate HomeAsync implementation.'
}
$homeMethod = $controller.Substring($homeStart, $homeEnd - $homeStart)

Assert-Contains $homeMethod 'IMC_StartHoming' 'HomeAsync must start the native homing mode.'
Assert-Contains $homeMethod 'IMC_GetHomingStatus' 'HomeAsync must wait for the native homing result.'
Assert-Contains $homeMethod 'IMC_FinishHoming' 'HomeAsync must exit the native homing mode.'
Assert-Contains $homeMethod 'HOME_SUCESS' 'HomeAsync must only report success for the native success status.'
Assert-Contains $homeMethod 'const short homeMethod = 28' 'Production homing must always use negative Home-switch method 28.'
Assert-NotContains $homeMethod 'DisableHardwareLimits' 'HomeAsync must not disable hardware limits.'
Assert-NotContains $homeMethod 'SetZero(axis)' 'Hardware homing must not be replaced by a session-only SetZero call.'
Assert-NotContains $controller 'DisableHardwareLimits' 'Application code must never disable hardware limits.'

$finishCheck = @'
if (finishResult != ImcApi.EXE_SUCCESS)
                        {
                            Log($"[Motion Error] Finish Homing Axis {axis} failed: Code 0x{finishResult:X8}.");
                            return false;
                        }
                        homingModeActive = false;
'@
Assert-Contains $homeMethod $finishCheck 'Cleanup must remain armed until FinishHoming succeeds.'

$moveStart = $controller.IndexOf('public bool MoveAbsolute')
$moveEnd = $controller.IndexOf('public bool MoveRelative', $moveStart)
if ($moveStart -lt 0 -or $moveEnd -le $moveStart)
{
    throw 'Could not locate MoveAbsolute implementation.'
}
$moveAbsolute = $controller.Substring($moveStart, $moveEnd - $moveStart)
Assert-Contains $moveAbsolute '!axisState.IsHomed' 'Absolute moves must be blocked until hardware homing succeeds.'

$api = Get-Content -LiteralPath (Join-Path $repositoryRoot 'BeeMotionModule\Core\ImcApi.cs') -Raw
Assert-Contains $api 'AX_HM_BIT = (0x00002000)' 'The normalized HOME axis-status bit must be declared.'

$homingConfig = Get-Content -LiteralPath (Join-Path $repositoryRoot 'BeeMotionModule\Models\HomingConfig.cs') -Raw
Assert-Contains $homingConfig 'HomeMethod { get; set; } = 28' 'The default must be negative-direction home-switch method 28.'

$sequence = Get-Content -LiteralPath (Join-Path $repositoryRoot 'BeevisionSolution\Controller\MotionSequenceManager.cs') -Raw
Assert-Contains $sequence 'WaitForServoOnAsync' 'The servo sequence must wait for the actual servo status.'

$motionView = Get-Content -LiteralPath (Join-Path $repositoryRoot 'BeevisionSolution\Views\MotionControlView.xaml') -Raw
Assert-Contains $motionView 'Home Sensor Negative (28)' 'The UI must expose the selected DS402 method 28 explicitly.'

Write-Output 'Motion homing contract checks passed.'
