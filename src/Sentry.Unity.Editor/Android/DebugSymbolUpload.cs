using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Sentry.Extensibility;
using Sentry.Unity.Integrations;
using UnityEditor;

namespace Sentry.Unity.Editor.Android
{
    internal class DebugSymbolUpload
    {
        private readonly IDiagnosticLogger _logger;

        internal const string RelativeBuildOutputPathOld = "Temp/StagingArea/symbols";
        internal const string RelativeGradlePathOld = "Temp/gradleOut";
        internal const string RelativeBuildOutputPathNew = "Library/Bee/artifacts/Android";
        internal const string RelativeAndroidPathNew = "Library/Bee/Android";

        private readonly string _unityProjectPath;
        private readonly string _gradleProjectPath;
        private readonly string _gradleScriptPath;
        private readonly bool _isExporting;
        private readonly bool _isMinifyEnabled;

        private readonly SentryCliOptions? _cliOptions;
        private readonly List<string> _symbolUploadPaths;
        private readonly string _mappingFilePath;

        private const string SymbolUploadTaskStartComment = "// Autogenerated Sentry symbol upload task [start]";
        private const string SymbolUploadTaskEndComment = "// Autogenerated Sentry symbol upload task [end]";
        private const string SentryCliMarker = "SENTRY_CLI";
        private const string UploadArgsMarker = "UPLOAD_ARGS";
        private const string MappingPathMarker = "MAPPING_PATH";

        private string _symbolUploadTaskFormat
        {
            get
            {
                var stringBuilder = new StringBuilder();
                stringBuilder.AppendLine("// Credentials and project settings information are stored in the sentry.properties file");
                stringBuilder.AppendLine("task sentryUploadSymbols {");
                stringBuilder.AppendLine("    doLast {");
                if (_isExporting)
                {
                    stringBuilder.AppendLine("        println 'Uploading symbols to Sentry.'");
                }
                else
                {
                    var logsDir = $"{ConvertSlashes(_unityProjectPath)}/Logs";
                    Directory.CreateDirectory(logsDir);
                    stringBuilder.AppendLine("        println 'Uploading symbols to Sentry. You can find the full log in ./Logs/sentry-symbols-upload.log (the file content may not be strictly sequential because it\\'s a merge of two streams).'");
                    stringBuilder.AppendLine($"        def sentryLogFile = new FileOutputStream('{logsDir}/sentry-symbols-upload.log')");
                }
                stringBuilder.AppendLine("        exec {");
                stringBuilder.AppendLine("            environment 'SENTRY_PROPERTIES', file(\"${rootDir}/sentry.properties\").absolutePath");
                stringBuilder.AppendLine($"            executable {SentryCliMarker}");
                stringBuilder.AppendLine($"            args = ['debug-files', 'upload'{UploadArgsMarker}]");
                if (!_isExporting)
                {
                    stringBuilder.AppendLine("            standardOutput sentryLogFile");
                    stringBuilder.AppendLine("            errorOutput sentryLogFile");
                }
                stringBuilder.AppendLine("        }");
                CheckMapping(stringBuilder);
                stringBuilder.AppendLine("    }");
                stringBuilder.AppendLine("}");
                stringBuilder.AppendLine(string.Empty);
                stringBuilder.AppendLine("tasks.assembleDebug.finalizedBy sentryUploadSymbols");
                stringBuilder.AppendLine("tasks.assembleRelease.finalizedBy sentryUploadSymbols");
                return stringBuilder.ToString();
            }
        }

        public DebugSymbolUpload(IDiagnosticLogger logger,
            SentryCliOptions? cliOptions,
            string unityProjectPath,
            string gradleProjectPath,
            bool isExporting = false,
            bool minifyEnabled = false,
            IApplication? application = null)
        {
            _logger = logger;

            _unityProjectPath = unityProjectPath;
            _gradleProjectPath = gradleProjectPath;
            _gradleScriptPath = Path.Combine(_gradleProjectPath, "launcher/build.gradle");
            _isExporting = isExporting;
            _isMinifyEnabled = minifyEnabled;

            _cliOptions = cliOptions;
            _symbolUploadPaths = GetSymbolUploadPaths(application);
            _mappingFilePath = GetMappingFilePath(application);
        }

        public void AppendUploadToGradleFile(string sentryCliPath)
        {
            RemoveUploadFromGradleFile();

            _logger.LogInfo("Appending debug symbols upload task to gradle file.");

            sentryCliPath = ConvertSlashes(sentryCliPath);
            if (!File.Exists(sentryCliPath))
            {
                throw new FileNotFoundException("Failed to find sentry-cli", sentryCliPath);
            }

            var uploadDifArguments = ", '--il2cpp-mapping'";
            if (_cliOptions != null && _cliOptions.UploadSources)
            {
                uploadDifArguments += ", '--include-sources'";
            }

            if (_isExporting)
            {
                uploadDifArguments += ", project.rootDir";
                sentryCliPath = $"file(\"${{rootDir}}/{Path.GetFileName(sentryCliPath)}\").absolutePath";
            }
            else
            {
                sentryCliPath = $"'{sentryCliPath}'";
                foreach (var symbolUploadPath in _symbolUploadPaths)
                {
                    if (Directory.Exists(symbolUploadPath))
                    {
                        uploadDifArguments += $", '{ConvertSlashes(symbolUploadPath)}'";
                    }
                    else
                    {
                        throw new DirectoryNotFoundException($"Failed to find the symbols directory at {symbolUploadPath}");
                    }
                }
            }

            var symbolUploadText = _symbolUploadTaskFormat;
            symbolUploadText = symbolUploadText.Trim();
            symbolUploadText = symbolUploadText.Replace(SentryCliMarker, sentryCliPath);
            symbolUploadText = symbolUploadText.Replace(UploadArgsMarker, uploadDifArguments);
            symbolUploadText = symbolUploadText.Replace(MappingPathMarker, _mappingFilePath);

            using var streamWriter = File.AppendText(_gradleScriptPath);
            streamWriter.WriteLine(SymbolUploadTaskStartComment);
            streamWriter.WriteLine(symbolUploadText);
            streamWriter.WriteLine(SymbolUploadTaskEndComment);
        }

        private string LoadGradleScript()
        {
            if (!File.Exists(_gradleScriptPath))
            {
                throw new FileNotFoundException($"Failed to find the gradle config.", _gradleScriptPath);
            }
            return File.ReadAllText(_gradleScriptPath);
        }

        public void RemoveUploadFromGradleFile()
        {
            _logger.LogDebug("Removing the upload task from the gradle project.");
            var gradleBuildFile = LoadGradleScript();
            if (!gradleBuildFile.Contains("sentry.properties"))
            {
                _logger.LogDebug("No previous upload task found.");
                return;
            }

            var regex = new Regex(Regex.Escape(SymbolUploadTaskStartComment) + ".*" + Regex.Escape(SymbolUploadTaskEndComment), RegexOptions.Singleline);
            gradleBuildFile = regex.Replace(gradleBuildFile, "");

            using var streamWriter = File.CreateText(_gradleScriptPath);
            streamWriter.Write(gradleBuildFile);
        }

        public void TryCopySymbolsToGradleProject(IApplication? application = null)
        {
            if (!_isExporting)
            {
                return;
            }

            _logger.LogInfo("Copying debug symbols to exported gradle project.");
            var targetRoot = Path.Combine(_gradleProjectPath, "symbols");
            foreach (var symbolUploadPath in _symbolUploadPaths)
            {
                // Seems like not all paths exist all the time... e.g. Unity 2021.2.21 misses RelativeAndroidPathNew.
                if (!Directory.Exists(symbolUploadPath))
                {
                    continue;
                }
                foreach (var sourcePath in Directory.GetFiles(symbolUploadPath, "*.so", SearchOption.AllDirectories))
                {
                    var targetPath = sourcePath.Replace(symbolUploadPath, targetRoot);
                    _logger.LogDebug("Copying '{0}' to '{1}'", sourcePath, targetPath);

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                    File.Copy(sourcePath, targetPath, true);
                }
            }
        }

        internal List<string> GetSymbolUploadPaths(IApplication? application = null)
        {
            var paths = new List<string>();
            if (IsNewBuildingBackend(application))
            {
                _logger.LogInfo("Unity version 2021.2 or newer detected. Root for symbols upload: 'Library'.");
                paths.Add(Path.Combine(_unityProjectPath, RelativeBuildOutputPathNew));
                paths.Add(Path.Combine(_unityProjectPath, RelativeAndroidPathNew));
            }
            else
            {
                _logger.LogInfo("Unity version 2021.1 or older detected. Root for symbols upload: 'Temp'.");
                paths.Add(Path.Combine(_unityProjectPath, RelativeBuildOutputPathOld));
                paths.Add(Path.Combine(_unityProjectPath, RelativeGradlePathOld));
            }
            return paths;
        }

        // Starting from 2021.2 Unity caches the build output inside 'Library' instead of 'Temp'
        internal static bool IsNewBuildingBackend(IApplication? application = null) => SentryUnityVersion.IsNewerOrEqualThan("2021.2", application);

        // Gradle doesn't support backslashes on path (Windows) so converting to forward slashes
        internal static string ConvertSlashes(string path) => path.Replace(@"\", "/");

        private void CheckMapping(StringBuilder stringBuilder)
        {
            if (!_isMinifyEnabled)
                return;

            stringBuilder.AppendLine("        println 'Uploading mapping file to Sentry.'");
            if (!_isExporting)
            {
                var logsDir = $"{ConvertSlashes(_unityProjectPath)}/Logs";
                Directory.CreateDirectory(logsDir);
                stringBuilder.AppendLine($"        def mappingLogFile = new FileOutputStream('{logsDir}/sentry-mapping-upload.log')");
            }
            stringBuilder.AppendLine("        exec {");
            stringBuilder.AppendLine("            environment 'SENTRY_PROPERTIES', file(\"${rootDir}/sentry.properties\").absolutePath");
            stringBuilder.AppendLine($"            executable {SentryCliMarker}");
            stringBuilder.AppendLine($"            args = ['upload-proguard', {MappingPathMarker}]");
            if (!_isExporting)
            {
                stringBuilder.AppendLine("            standardOutput mappingLogFile");
                stringBuilder.AppendLine("            errorOutput mappingLogFile");
            }
            stringBuilder.AppendLine("        }");
        }

        private string GetMappingFilePath(IApplication? application)
        {
            var gradleRelativePath = IsNewBuildingBackend(application)
                ? "Library/Bee/Android/Prj/IL2CPP/Gradle"
                : "Temp/gradleOut";

            string mappingPathFormat;
            var buildType = EditorUserBuildSettings.development ? "debug" : "release";
            if (_isExporting)
            {
                mappingPathFormat =
                    "file(\"${rootDir}/launcher/build/outputs/mapping/{0}/mapping.txt\").absolutePath";
            }
            else
            {
                var gradleProjectPath = Path.Combine(_unityProjectPath, gradleRelativePath);
                mappingPathFormat = Path.Combine(gradleProjectPath, "launcher/build/outputs/mapping/{0}/mapping.txt");
                mappingPathFormat = $"'{mappingPathFormat}'";
            }

            var mappingPath = mappingPathFormat.Replace("{0}", buildType);
            return mappingPath;
        }
    }
}
