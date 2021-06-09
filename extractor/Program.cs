using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CommandLine;
using Newtonsoft.Json;

namespace extractor
{
    public enum OutputMode
    {
        stdout = 0,
        stdoutBytes = 1,
        file = 2
    }

    public class ProgramOptions
    {
        [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
        public bool Verbose { get; set; }

        [Option("OutputMode", Required = true, HelpText = "select extraction ouptut mode, stdout or file")]
        public OutputMode outputMode { get; set; }

        [Option('o', "out", Required = false, HelpText = "Set output file path")]
        public string outputPath { get; set; }

        [Option("log", Required = false, HelpText = "Set log output path")]
        public string logOutputPath { get; set; }

        // [Option("--file", Required = true, Separator = ' ')]
        [Value(0, Required = true, HelpText = "dll file/directory to extract data")]
        public IEnumerable<string> targetFiles { get; set; }

        [Option("extraTypes", Required = false, HelpText = "Force extra types to be included")]
        public string ExtraTypes { get; set; }
    }

    internal class Program
    {
        private static int Main(string[] args)
        {
            var commandline = Parser.Default.ParseArguments<ProgramOptions>(args);
            var exitCode = 1;
            commandline.WithParsed(option =>
            {
                if (option.logOutputPath != null) Log.SetOutput(option.logOutputPath, option.Verbose);
                try
                {
                    Log.Info("Extracting data from");
                    foreach (var file in option.targetFiles)
                        Log.Info(file);
                    var assemblies = AssemblyLoader.Load(option.targetFiles);
                    Log.Info("extracting data...");
                    var types = !string.IsNullOrEmpty(option.ExtraTypes)
                        ? option.ExtraTypes.Split(' ')
                        : new string[0];
                    Log.Debug($"Found {types.Length} extra types:");
                    foreach (var s in types) Log.Debug("  " + s);
                    var parseResult = Extractor.parse(assemblies, types);
                    Log.Info($"Completed extracting data, data count: {parseResult.Count}");

                    var result = new Dictionary<string, TypeInfo>();
                    foreach (var pair in parseResult)
                    {
                        var typeInfo = pair.Value;
                        if (result.ContainsKey(typeInfo.typeIdentifier))
                            continue;
                        result.Add(typeInfo.typeIdentifier, typeInfo);
                    }

                    var serializerSetting = new JsonSerializerSettings();
                    serializerSetting.Formatting = Formatting.None;
                    serializerSetting.NullValueHandling = NullValueHandling.Ignore;
                    serializerSetting.DefaultValueHandling = DefaultValueHandling.Ignore;

                    var serializedObject = JsonConvert.SerializeObject(result.Select(d => d.Value), serializerSetting);
                    Log.Info($"serialized Object string length: {serializedObject.Length}");

                    switch (option.outputMode)
                    {
                        case OutputMode.stdout:
                            Console.WriteLine(serializedObject);
                            break;

                        case OutputMode.stdoutBytes:
                            var utf8bytes = Encoding.UTF8.GetBytes(serializedObject);
                            Log.Info($"serialized text bytes length: {utf8bytes.Length}");
                            Console.OpenStandardOutput().Write(utf8bytes, 0, utf8bytes.Length);
                            break;

                        case OutputMode.file:
                            var path = option.outputPath;
                            File.WriteAllText(path, serializedObject);
                            break;
                    }

                    Log.Info("Extraction completed!");
                    exitCode = 0;
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                    exitCode = 1;
                }
            });

            return exitCode;
        }
    }
}