using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sentry.Extensibility;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Sentry.Unity.Editor
{
    public static class PerformanceAutoInstrumentation
    {
        private const string PlayerAssembly = "Assembly-CSharp.dll";
        private const string OutputDirectory = "PlayerScriptAssemblies"; // There are multiple directories involved - we want this one in particular

        private static readonly string PlayerAssemblyPath;
        private static readonly string SentryUnityAssemblyPath;

        private static IDiagnosticLogger? Logger;

        static PerformanceAutoInstrumentation()
        {
            PlayerAssemblyPath = Path.Combine(Application.dataPath, "..", "Library", OutputDirectory, PlayerAssembly);
            SentryUnityAssemblyPath = Path.GetFullPath(Path.Combine("Packages", SentryPackageInfo.GetName(), "Runtime", "Sentry.Unity.dll"));
        }

        [InitializeOnLoadMethod]
        public static void InitializeCompilationCallback()
        {
            var options = SentryScriptableObject.Load<ScriptableSentryUnityOptions>(ScriptableSentryUnityOptions.GetConfigPath());
            if (options == null || options.TracesSampleRate <= 0 && !options.AutoInstrumentPerformance)
            {
                return;
            }

            Logger = options.ToSentryUnityOptions(isBuilding: true).DiagnosticLogger;

            CompilationPipeline.assemblyCompilationFinished += (assemblyPath, compilerMessages) =>
            {
                Debug.Log(assemblyPath);
                // Adding the output directory to the check because there are two directories involved in building. We specifically want 'PlayerScriptAssemblies'
                if (assemblyPath.Contains(Path.Combine(OutputDirectory, PlayerAssembly)))
                {
                    Logger?.LogInfo("Compilation of '{0}' finished. Running Performance Auto Instrumentation.", PlayerAssembly);
                    var stopwatch = new Stopwatch();
                    stopwatch.Start();

                    try
                    {
                        ModifyPlayerAssembly(PlayerAssemblyPath, SentryUnityAssemblyPath);
                    }
                    catch (Exception e)
                    {
                        Logger?.LogError("Failed to add the performance auto instrumentation. " +
                                         "The assembly has not been modified.", e);
                    }

                    stopwatch.Stop();
                    Logger?.LogInfo("Auto Instrumentation finished in '{0}'.", stopwatch.Elapsed);
                }
            };
        }

        [MenuItem("Tools/Modify Assembly")]
        public static void JustForDevelopingSoIDontHaveToBuildEveryTimeAndICanCompareTheAssemblies()
        {
            var options = SentryScriptableObject.Load<ScriptableSentryUnityOptions>(ScriptableSentryUnityOptions.GetConfigPath());
            Logger = new UnityLogger(options!.ToSentryUnityOptions(true));

            var playerAssemblyPath = Path.Combine(Application.dataPath, "..", "Builds",
                "Test.app", "Contents", "Resources", "Data", "Managed", PlayerAssembly);

            var workingPlayerAssemblyPath = playerAssemblyPath.Replace(PlayerAssembly, "Assembly-CSharp-WIP.dll");
            if (File.Exists(workingPlayerAssemblyPath))
            {
                File.Delete(workingPlayerAssemblyPath);
            }

            File.Copy(playerAssemblyPath, workingPlayerAssemblyPath);

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            try
            {
                ModifyPlayerAssembly(workingPlayerAssemblyPath, SentryUnityAssemblyPath);
            }
            catch (Exception e)
            {
                Logger?.LogError("Failed to add the performance auto instrumentation. " +
                                 "The player assembly has not been modified.", e);
            }

            stopwatch.Stop();
            Logger?.LogInfo("Finished Adding performance auto instrumentation in '{0}'", stopwatch.Elapsed);
        }

        private static TypeDefinition GetTypeDefinition(ModuleDefinition module, Type type)
        {
            var typeDefinition = module.GetType(type.FullName);
            if (typeDefinition is null)
            {
                throw new Exception($"Failed to get type '{type.FullName}' from module '{module.Name}'");
            }

            return typeDefinition;
        }

        private static TypeReference ImportReference(ModuleDefinition module, Type type)
        {
            var reference = module.ImportReference(type);
            if (reference is null)
            {
                throw new Exception($"Failed to import '{type.FullName}' into '{module.Name}'");
            }

            return reference;
        }

        private static MethodDefinition GetMethodDefinition(TypeDefinition typeDefinition, string name, Type[]? requiredParameters = null)
        {
            foreach (var method in typeDefinition.Methods)
            {
                if (method?.Name == name)
                {
                    switch (requiredParameters)
                    {
                        case null when !method.HasParameters:
                            return method;
                        case null:
                            continue;
                    }

                    var hasMatchingParameters = true;
                    foreach (var parameter in requiredParameters)
                    {
                        var parameterDefinitions = method.Parameters.Where(p =>
                            string.Equals(p.ParameterType.FullName, parameter.FullName, StringComparison.CurrentCulture)).ToList();
                        if(parameterDefinitions.Count == 0)
                        {
                            hasMatchingParameters = false;
                            break;
                        }
                    }

                    if (hasMatchingParameters)
                    {
                        return method;
                    }
                }
            }

            throw new Exception(
                $"Failed to find method '{name}' " +
                $"in '{typeDefinition.FullName}' " +
                $"with parameters: '{(requiredParameters is not null ? string.Join(",", requiredParameters.ToList()) : "none")}'");
        }

        private static void ModifyPlayerAssembly(string playerAssemblyPath, string sentryUnityAssemblyPath)
        {
            if (!File.Exists(playerAssemblyPath))
            {
                throw new FileNotFoundException($"Failed to find '' at '{playerAssemblyPath}' not found.");
            }

            if (!File.Exists(sentryUnityAssemblyPath))
            {
                throw new FileNotFoundException($"Failed to find '{Path.GetFileName(sentryUnityAssemblyPath)}' at '{sentryUnityAssemblyPath}'.");
            }

            var (sentryModule, _) = ModuleReaderWriter.Read(sentryUnityAssemblyPath);
            var (playerModule, hasSymbols) = ModuleReaderWriter.Read(playerAssemblyPath);

            var sentryMonoBehaviourDefinition = GetTypeDefinition(sentryModule, typeof(SentryMonoBehaviour));
            var monoBehaviourReference = playerModule.ImportReference(typeof(MonoBehaviour));

            var getInstanceMethod = playerModule.ImportReference(GetMethodDefinition(sentryMonoBehaviourDefinition, "get_Instance"));
            var startAwakeSpanMethod = playerModule.ImportReference(GetMethodDefinition(sentryMonoBehaviourDefinition, "StartAwakeSpan", new [] { typeof(MonoBehaviour)}));
            var finishAwakeSpanMethod = playerModule.ImportReference(GetMethodDefinition(sentryMonoBehaviourDefinition, "FinishAwakeSpan"));

            foreach (var type in playerModule.GetTypes())
            {
                if (type.BaseType?.FullName != monoBehaviourReference?.FullName)
                {
                    continue;
                }

                Logger?.LogDebug("Checking: '{0}'", type.FullName);

                foreach (var method in type.Methods.Where(method => method.Name == "Awake"))
                {
                    Logger?.LogDebug("Found 'Awake' method.");

                    if (method.Body is null)
                    {
                        Logger?.LogDebug("Method body is null. Skipping.");
                        continue;
                    }

                    if (method.Body.Instructions is null)
                    {
                        Logger?.LogDebug("Instructions are null. Skipping.");
                        continue;
                    }

                    var processor = method.Body.GetILProcessor();

                    // Adding in reverse order because we're inserting *before* the 0ths element
                    processor.InsertBefore(method.Body.Instructions[0], processor.Create(OpCodes.Callvirt, startAwakeSpanMethod));
                    processor.InsertBefore(method.Body.Instructions[0], processor.Create(OpCodes.Ldarg_0));
                    processor.InsertBefore(method.Body.Instructions[0], processor.Create(OpCodes.Call, getInstanceMethod));

                    // We're checking the instructions for OpCode.Ret so we can insert the finish span instruction before it.
                    // Iterating over the collection backwards because we're modifying it's length
                    for (var i = method.Body.Instructions.Count - 1; i >= 0; i--)
                    {
                        if (method.Body.Instructions[i].OpCode == OpCodes.Ret)
                        {
                            processor.InsertBefore(method.Body.Instructions[i], processor.Create(OpCodes.Call, getInstanceMethod));
                            processor.InsertBefore(method.Body.Instructions[i+1], processor.Create(OpCodes.Call, finishAwakeSpanMethod)); // +1 because return just moved one back
                        }
                    }
                }
            }

            Logger?.LogInfo("Applying Auto Instrumentation by overwriting '{0}'.", Path.GetFileName(playerAssemblyPath));
            ModuleReaderWriter.Write(null, hasSymbols, playerModule, playerAssemblyPath);
        }
    }
}
