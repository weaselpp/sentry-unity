using System;
using System.IO;
using Sentry.Extensibility;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using System.Diagnostics;

namespace Sentry.Unity.Editor.Native
{
    public static class BuildPostProcess
    {
        [PostProcessBuild(1)]
        public static void OnPostProcessBuild(BuildTarget target, string executablePath)
        {
            if (target is not (BuildTarget.StandaloneWindows or BuildTarget.StandaloneWindows64))
            {
                return;
            }

            var options = ScriptableSentryUnityOptions.LoadSentryUnityOptions(BuildPipeline.isBuildingPlayer);
            var logger = options?.DiagnosticLogger ?? new UnityLogger(options ?? new SentryUnityOptions());

            try
            {
                if (PlayerSettings.GetScriptingBackend(EditorUserBuildSettings.selectedBuildTargetGroup) != ScriptingImplementation.IL2CPP)
                {
                    logger.LogWarning("Failed to enable Native support - only availabile with IL2CPP scripting backend.");
                    return;
                }

                if (options?.Validate() is not true)
                {
                    logger.LogWarning("Failed to validate Sentry Options. Native support disabled.");
                    return;
                }

                if (!options.WindowsNativeSupportEnabled)
                {
                    logger.LogDebug("Windows Native support disabled through the options.");
                    return;
                }

                var projectDir = Path.GetDirectoryName(executablePath);
                addCrashHandler(logger, projectDir);
                uploadDebugSymbols(logger, projectDir, Path.GetFileName(executablePath));

            }
            catch (Exception e)
            {
                logger.LogError("Failed to add the Sentry native integration to the built application", e);
                throw new BuildFailedException("Sentry Native BuildPostProcess failed");
            }
        }

        private static void addCrashHandler(IDiagnosticLogger logger, string projectDir)
        {
            var crashpadPath = Path.GetFullPath(Path.Combine("Packages", SentryPackageInfo.GetName(), "Plugins",
                "Windows", "Sentry", "crashpad_handler.exe"));
            var targetPath = Path.Combine(projectDir, Path.GetFileName(crashpadPath));
            logger.LogInfo($"Copying the native crash handler '{Path.GetFileName(crashpadPath)}' to the output directory");
            File.Copy(crashpadPath, targetPath, true);
        }

        private static void uploadDebugSymbols(IDiagnosticLogger logger, string projectDir, string executableName)
        {
            var cliOptions = SentryCliOptions.LoadCliOptions();
            if (!cliOptions.Validate(logger))
                return;

            var propertiesFile = SentryCli.CreateSentryProperties(projectDir, cliOptions);

            logger.LogInfo($"Uploading debuging information using sentry-cli in {projectDir}");

            var paths = "";
            Func<string, bool> addPath = (string name) =>
            {
                var fullPath = Path.Combine(projectDir, name);
                if (Directory.Exists(fullPath) || File.Exists(fullPath))
                {
                    paths += " " + name;
                    logger.LogDebug($"Adding '{name}' to the debug-info upload");
                    return true;
                }
                else
                {
                    logger.LogWarning($"Coudn't find '{name}' - debug symbol upload will be incomplete");
                    return false;
                }
            };

            addPath(executableName);
            addPath("GameAssembly.dll");
            addPath("UnityPlayer.dll");
            addPath(Path.GetFileNameWithoutExtension(executableName) + "_BackUpThisFolder_ButDontShipItWithYourGame");

            // no debug info in sentry.dll but sentry.io will complain about a missing file...
            // addPath(Path.GetFileNameWithoutExtension(executableName) + "_Data/Plugins/x86_64/sentry.dll");

            // Configure the process using the StartInfo properties.
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = SentryCli.SetupSentryCli(),
                    WorkingDirectory = projectDir,
                    Arguments = "upload-dif " + paths,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.EnvironmentVariables["SENTRY_PROPERTIES"] = propertiesFile;
            process.OutputDataReceived += (sender, args) => logger.LogDebug($"sentry-cli: {args.Data.ToString()}");
            process.ErrorDataReceived += (sender, args) => logger.LogWarning($"sentry-cli: {args.Data.ToString()}");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
        }
    }
}
