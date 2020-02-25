using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TachyonCommon;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;
using Debug = UnityEngine.Debug;

[InitializeOnLoad]
public static class TachyonDesignTimeBinder {
    private static bool _bound = false;
    private static long _lastBuildTime;
    
    static TachyonDesignTimeBinder() {

        if (!_bound) {
            CompilationPipeline.assemblyCompilationStarted += OnAssemblyCompilationStarted;
            _lastBuildTime = DateTime.Now.Ticks;
            _bound = true;
        }

    }

    private static void OnAssemblyCompilationStarted(string builtAssembly) {
        
        if(!builtAssembly.Contains("Assembly-CSharp.dll")) return;
        
        var fileInfo = new FileInfo(builtAssembly);
        if (!fileInfo.Exists) return;

        // Don't get caught in rebuild cycle after detecting own changes.
        if(DateTime.Now.Ticks - _lastBuildTime < TimeSpan.FromSeconds(5).Ticks) return;
        _lastBuildTime = DateTime.Now.Ticks;

        var assembly = Assembly.LoadFile(fileInfo.FullName);
        var interfaceTypes = assembly.GetTypes()
            .Where(t => t.IsInterface);
        foreach (var foundType in interfaceTypes) {
            var isInterop = foundType
                .GetCustomAttributes(false)
                .Any(a => a is GenerateBindingsAttribute);
            if (isInterop) {
                var interopInterfaceFile =
                    FindScriptForInterface(foundType);
                if (interopInterfaceFile == null)
                    Debug.LogError("Interface " + interopInterfaceFile + " not found.");
                else
                    UpdateTachyonBindings(interopInterfaceFile);
            }
        }
    }

    private static FileInfo FindScriptForInterface(Type interfaceType) {
        var assetScriptFiles = Directory.GetFiles(
            "Assets/",
            "*.cs",
            SearchOption.AllDirectories);
        var packageScriptFiles = Directory.GetFiles(
            "Packages/",
            "*.cs",
            SearchOption.AllDirectories);
        var scriptFiles = assetScriptFiles
            .Concat(packageScriptFiles);

        foreach (var script in scriptFiles) {

            var code = File.ReadAllText(script);
            var hasInterop = code.Contains("[GenerateBindings]");
            var hasInterface = Regex.IsMatch(
                code,
                "interface\\s*" + interfaceType.Name
            );

            if (hasInterop && hasInterface)
                return new FileInfo(script);
        }

        return null;
    }

    private static void UpdateTachyonBindings(FileInfo interopInterfaceFile) {
        var binderFile = FindBinderExecFile();
        if (binderFile == null) {
            Debug.LogError("Tachyon-Binder missing.");
        } else {
            foreach (ITachyonInstalledFlag flag in GetTachyonInstallFlags()) {
                if (interopInterfaceFile.Directory?.FullName == null) return;
                var interopInterfacePath = new DirectoryInfo(
                    interopInterfaceFile.Directory.FullName);
                ProcessStart(
                    binderFile,
                    interopInterfaceFile,
                    interopInterfacePath,
                    flag.ActivationArgument
                );
            }
        }
    }

    static ITachyonInstalledFlag[] GetTachyonInstallFlags() {
        
        var result = new List<ITachyonInstalledFlag>();
        var aAppDomain = AppDomain.CurrentDomain;
        var assemblies = aAppDomain.GetAssemblies()
            .Where(a => a.FullName.Contains("Tachyon"));
        foreach (var assembly in assemblies)
        {
            var types = assembly.GetTypes();
            foreach (var type in types) {
                if (typeof(ITachyonInstalledFlag).IsAssignableFrom(type) && !type.IsInterface) {
                    var installFlag = Activator.CreateInstance(type) as ITachyonInstalledFlag;
                    result.Add(installFlag);
                }
            }
        }
        
        return result.ToArray();
    }

    private static void ProcessStart(
        FileInfo executableFile, 
        FileInfo interfaceFile, 
        DirectoryInfo outputDirectory,
        string activationArg
    ) {

        var canRun = executableFile != null && interfaceFile != null && outputDirectory != null; 
        if(!canRun) return;
        
        var execFile = executableFile.FullName;
        ProcessStartInfo startInfo;
        
        try {
            startInfo = new ProcessStartInfo(execFile);
        } catch {
            // hack because of this: https://github.com/dotnet/corefx/issues/10361
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                execFile = execFile.Replace("&", "^&");
                startInfo = new ProcessStartInfo("cmd", $"/c start {execFile}") { CreateNoWindow = true };
            } else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                startInfo = new ProcessStartInfo("xdg-open", execFile);
            } else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
                startInfo = new ProcessStartInfo("open", execFile);
            } else {
                throw;
            }
        }

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;

        // Interface file.
        startInfo.Arguments += interfaceFile.FullName;
        startInfo.Arguments += " ";
        
        // Generated Biding folder.
        if (!Directory.Exists(outputDirectory.FullName))
            Directory.CreateDirectory(outputDirectory.FullName);
        startInfo.Arguments += outputDirectory.FullName;
        
        // Generate client binding.
        startInfo.Arguments += " " + activationArg;
        
        // Run binding generation process.
        var process = Process.Start(startInfo);
        
        // Output
        var stdout = process.StandardOutput.ReadToEnd();
        if(!string.IsNullOrEmpty(stdout))
            Debug.Log(stdout);
        var stdErr = process.StandardError.ReadToEnd();
        if(!string.IsNullOrEmpty(stdErr))
            Debug.LogError(stdErr);

    }

    private static FileInfo FindBinderExecFile()
    {

        var execFileName = String.Empty;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            execFileName = "Tachyon-Binder-Win.exe";
        } else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            execFileName = "Tachyon-Binder-Linux";
        } else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
            execFileName = "Tachyon-Binder-OSX";
        } else {
            throw new PlatformNotSupportedException();
        }
        
        var currentDirectory = new FileInfo(".");
        var files = Directory.GetFiles(
            currentDirectory.FullName,
            execFileName,
            SearchOption.AllDirectories);
    
        if (!files.Any())
            return null;
    
        var tachyonBinder = files.Single();
        return new FileInfo(tachyonBinder);
    }

}
