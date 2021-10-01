using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;
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

		private static Config ReadConfig()
		{
			string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "config.yaml");
			using StreamReader text = new(path);

			return new DeserializerBuilder()
				.WithNamingConvention(UnderscoredNamingConvention.Instance)
				.Build()
				.Deserialize<Config>(text);
		}

		[SuppressMessage("ReSharper", "UnusedMember.Global")]
		public static void Patch(AssemblyDefinition asm)
		{
			Logger.LogWarning("No DLLs should be patched, but the patch method was called. Assembly: " + asm);
		}

		[SuppressMessage("ReSharper", "UnusedMember.Global")]
		public static void Initialize()
		{
			Converter converter = new(Logger, ReadConfig());

			string[] directories = Directory.GetDirectories(Paths.PluginPath);
			var completions = new bool[directories.Length];
			for (int i = 0; i < directories.Length; ++i)
			{
				string directory = directories[i];
				int iCopy = i;

				ThreadPool.QueueUserWorkItem(_ =>
				{
					try
					{
						converter.PreCompile(directory);
						completions[iCopy] = true;
					}
					catch (Exception e)
					{
						Logger.LogError($"Failed to run precompile method on threadpool:\n{e}");
					}
				});
			}

			// Optimized (because this will run quite a few times) form of:
			// while (!completions.All(x => x))
			// 	Thread.Sleep(10);
			Sleep:
			Thread.Sleep(10);

			foreach (bool item in completions)
				if (!item)
					goto Sleep;
		}
	}
}
