using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEditor.iOS.Xcode;
using UnityEditor.iOS.Xcode.Extensions;

namespace Sentry.Unity.Editor
{
    public static class BuildPostProcess
    {
        private const string Include = "#include <Sentry/Sentry.h>\n#include \"SentryOptions.m\"\n";
        private const string Init = "\t\t[SentrySDK startWithOptions:GetOptions()];\n\n";

        // TODO: IMPORTANT! This HAS to match the location where unity copies the framework to and matches the location in the project
        private const string FrameworkLocation = "Frameworks/Plugins/iOS"; // The path where the framework is stored
        private const string FrameworkName = "Sentry.framework";

        [PostProcessBuild(1)]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS)
            {
                return;
            }

            var projectPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            var project = new PBXProject();
            project.ReadFromString(File.ReadAllText(projectPath));

            var targetGuid = project.GetUnityMainTargetGuid();

            AddSentryFramework(project, targetGuid);
            ModifyMain(projectPath);
            AddOptions(project, pathToBuiltProject);

            project.WriteToFile(projectPath);
        }

        public static void AddSentryFramework(PBXProject project, string targetGuid)
        {
            var fileGuid = project.AddFile(
                Path.Combine(FrameworkLocation, FrameworkName),
                Path.Combine(FrameworkLocation, FrameworkName));

            var unityLinkPhaseGuid = project.GetFrameworksBuildPhaseByTarget(targetGuid);

            project.AddFileToBuildSection(targetGuid, unityLinkPhaseGuid, fileGuid); // Link framework in 'Build Phases > Link Binary with Libraries'
            project.AddFileToEmbedFrameworks(targetGuid, fileGuid); // Embedding the framework because it's dynamic and needed at runtime

            project.SetBuildProperty(targetGuid, "FRAMEWORK_SEARCH_PATHS", "$(inherited)");
            project.AddBuildProperty(targetGuid, "FRAMEWORK_SEARCH_PATHS", $"$(PROJECT_DIR)/{FrameworkLocation}/");

            // project.AddBuildProperty(targetGuid, "OTHER_LDFLAGS", "-ObjC");
        }

        public static void ModifyMain(string projectPath)
        {
            var mainPath = Path.Combine(projectPath, "MainApp", "main.mm");
            if (!File.Exists(mainPath))
            {
                return;
            }

            var text = File.ReadAllText(mainPath);

            var includeRegex = new Regex(@"\#include \<Sentry\/Sentry\.h\>");
            if (includeRegex.Match(text).Success)
            {
                return;
            }

            text = Include + text;

            var initRegex = new Regex(@"int main\(int argc, char\* argv\[\]\)\n{\n\s+@autoreleasepool\n.\s+{\n");
            var match = initRegex.Match(text);
            if (match.Success)
            {
                text = text.Insert(match.Index + match.Length, Init);
            }

            File.WriteAllText(mainPath, text);
        }

        public static void AddOptions(PBXProject project, string projectPath)
        {
            var options = ScriptableSentryUnityOptions.LoadSentryUnityOptions();
            if (options is null)
            {
                return;
            }

            using StreamWriter sw = File.CreateText(Path.Combine(projectPath, "MainApp", "SentryOptions.m"));

            var templateLines = File.ReadAllLines("Assets/Plugins/Sentry/Template.txt");
            for (var i = 0; i < templateLines.Length; i++)
            {
                Debug.Log($"{templateLines[i]}");

                if (templateLines[i].Contains("dsn"))
                {
                    sw.WriteLine(templateLines[i].Replace("#", options.Dsn));
                    continue;
                }

                if (templateLines[i].Contains("enableAutoSessionTracking"))
                {
                    sw.WriteLine(templateLines[i].Replace("#", "NO"));
                    continue;
                }

                if (templateLines[i].Contains("debug"))
                {
                    sw.WriteLine(templateLines[i].Replace("#", "YES"));
                    continue;
                }

                sw.WriteLine(templateLines[i]);
            }

            var optionsGuid = project.AddFile(
                Path.Combine("MainApp", "SentryOptions.m"),
                Path.Combine("MainApp", "SentryOptions.m"));
        }

    }
}
