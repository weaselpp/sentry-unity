﻿param($path)

. ./scripts/integration-test/IntegrationGlobals.ps1

ShowIntroAndValidateRequiredPaths "True" "Build project" $path
# ============= STEP 2/4.1 BUILD PROJECT

Write-Output "Removing Log"
ClearUnityLog

Write-Output "Checking if Project has no errors "
Write-Host -NoNewline "Creating integration project:"
$UnityProcess = Start-Process -FilePath "$Global:UnityPath/$Unity" -ArgumentList "-batchmode", "-projectPath ", "$NewProjectPath", "-logfile", "$NewProjectLogPath/$LogFile", "-buildWindows64Player", "$NewProjectBuildPath/Test.exe", "-quit" -PassThru
Write-Output " OK"

WaitLogFileToBeCreated 30
Write-Output "Waiting for Unity to build the project."
TrackCacheUntilUnityClose($UnityProcess)

if ($UnityProcess.ExitCode -ne 0)
{
    Throw "Unity exited with code $($UnityProcess.ExitCode)"
}
else
{
    Write-Output ""
    Write-Output "Project Build!!"
    ShowCheck
}