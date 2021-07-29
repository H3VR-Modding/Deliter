using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using Ionic.Zip;
using Mono.Cecil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Deliter
{
	[SuppressMessage("ReSharper", "UnusedType.Global")]
	public static class Entrypoint
	{
		private static readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource("Deliter");

		[SuppressMessage("ReSharper", "UnusedMember.Global")]
		[SuppressMessage("ReSharper", "InconsistentNaming")]
		public static IEnumerable<string> TargetDLLs => Enumerable.Empty<string>();

		[SuppressMessage("ReSharper", "UnusedMember.Global")]
		public static void Patch(AssemblyDefinition asm)
		{
			Logger.LogWarning("No DLLs should be patched, but the patch method was called. Assembly: " + asm);
		}

		[SuppressMessage("ReSharper", "UnusedMember.Global")]
		public static void Initialize()
		{
			try
			{
				Run();
			}
			catch (Exception e)
			{
				Logger.LogFatal("A mod-agnostic error has occured. No additional mods will be processed: " + e);
			}
		}

		private static void Run()
		{
			Config config;
			{
				string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "config.yaml");
				using StreamReader text = new(path);

				config = new DeserializerBuilder()
					.WithNamingConvention(UnderscoredNamingConvention.Instance)
					.Build()
					.Deserialize<Config>(text);
			}

			Converter converter = new(config);

			foreach (string plugin in Directory.GetDirectories(Paths.PluginPath))
			{
				// Ensure it is a TS package
				if (!File.Exists(Path.Combine(plugin, "manifest.json")))
					continue;

				// Ignore package if the name is listed as ignorable
				string name = Path.GetFileName(plugin);
				string[] split = name.Split('-');
				if (split.Length == 2 && config.Ignore.Contains(split[1]))
				{
					Logger.LogDebug($"Ignoring '{name}' because it is in the ignore filter");
					continue;
				}

				using IEnumerator<string> mods = ((IEnumerable<string>) Directory.GetFiles(plugin, "*.deli", SearchOption.AllDirectories)).GetEnumerator();

				// No mods
				if (!mods.MoveNext())
					continue;

				string mod = mods.Current!;

				if (mods.MoveNext())
				{
					Logger.LogWarning($"'{name}' contained multiple .deli files. Skipping");
					continue;
				}

				try
				{
					Convert(converter, mod);
				}
				catch (Exception e)
				{
					Logger.LogError($"Could not convert '{name}':\n{e}");
				}

				Logger.LogInfo($"Converted '{name}' to a Mason project");
			}
		}

		private static void Convert(Converter converter, string path)
		{
			{
				string directory = Path.GetDirectoryName(path)!;
				string resources = Path.Combine(directory, "resources");
				if (Directory.Exists(resources))
					throw new IOException("Resources directory is already in use");

				using ZipFile zip = ZipFile.Read(path);

				foreach (ZipEntry entry in zip.Entries)
				{
					string fileName = entry.FileName;
					if (Path.GetExtension(fileName) != ".dll")
						continue;

					var buffer = new byte[(int) entry.UncompressedSize];

					using Stream raw = entry.OpenReader();
					raw.Read(buffer, 0, buffer.Length);

					using MemoryStream seekable = new(buffer);
					using ModuleDefinition module = ModuleDefinition.ReadModule(seekable);

					foreach (AssemblyNameReference reference in module.AssemblyReferences)
					{
						string name = reference.Name;
						if (name is not "Deli.Patcher" or "Deli.Setup")
							continue;

						throw new InvalidOperationException($"Assembly located at {entry.FileName} contains a reference to Deli ({name})");
					}
				}

				JObject manifest;
				const string manifestName = "manifest.json";
				{
					if (zip[manifestName] is not { } entry)
						throw new InvalidOperationException("Mod contained no " + manifestName);

					using Stream raw = entry.OpenReader();
					using StreamReader text = new(raw);
					using JsonTextReader json = new(text);

					manifest = JObject.Load(json);
				}

				{
					YamlStream project = converter.Convert(manifest);

					using StreamWriter text = new(Path.Combine(directory, "project.yaml"), false, Utility.UTF8NoBom);

					project.Save(text, false);
				}

				Directory.CreateDirectory(resources);
				try
				{
					zip.ExtractAll(resources);
				}
				catch (Exception e)
				{
					throw new IOException("Failed to extract all resources", e);
				}

				// Information has already been used
				File.Delete(Path.Combine(resources, manifestName));
			}

			// So we don't run again next launch
			// Don't delete the file to prevent someone who spent their lifetime on a mod but didn't make any backups from getting pissed
			File.Move(path, Path.ChangeExtension(path, "delite_this"));
		}
	}
}
