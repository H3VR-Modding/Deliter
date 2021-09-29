using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using Ionic.Zip;
using Mono.Cecil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Deliter
{
	internal class Converter
	{
		private static JsonSerializationException NewJsonException(JToken token, string message)
		{
			IJsonLineInfo info = token;

			return new JsonSerializationException(message, token.Path, info.LineNumber, info.LinePosition, null);
		}

		private static UnconvertableException NewUnconvertableException(JToken token, string message)
		{
			return new UnconvertableException(NewJsonException(token, message));
		}

		private static JObject ReadManifest(ZipEntry entry)
		{
			using Stream raw = entry.OpenReader();
			using StreamReader text = new(raw);
			using JsonTextReader json = new(text);

			return JObject.Load(json);
		}

		private static void CheckForDeliDlls(ZipFile zip)
		{
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
		}

		private static void ExtractResources(string resources, ZipFile zip)
		{
			Directory.CreateDirectory(resources);
			try
			{
				zip.ExtractAll(resources);
			}
			catch (Exception e)
			{
				throw new IOException("Failed to extract all resources", e);
			}
		}

		private readonly Config _config;

		private readonly ManualLogSource _logger;

		private readonly YamlStream _masonConfig = new(new YamlDocument(new YamlMappingNode
			{
				{
					"directories", new YamlMappingNode
					{
						{
							"bepinex", new YamlScalarNode(Paths.BepInExRootPath)
						},
						{
							"managed", new YamlScalarNode(Paths.ManagedPath)
						}
					}
				}
			}
		));

		public Converter(ManualLogSource logger, Config config)
		{
			_logger = logger;
			_config = config;
		}

		private YamlNode? ConvertAsset(JProperty asset)
		{
			if (asset is not {Name: { } path, Value: JValue {Value: string rawLoader}})
				throw NewJsonException(asset, "Invalid asset");

			string[] split = rawLoader.Split(':');
			if (split.Length != 2)
				throw NewJsonException(asset, "Loaders must be a mod GUID and loader name, separated by a single colon");

			// Ignore. This will be loaded by BepInEx, if applicable
			if (split[0] == "deli" && split[1] == "assembly")
				return null;

			string plugin = split[0];
			if (!_config.Plugins.TryGetValue(plugin, out Plugin convPlugin))
				throw NewUnconvertableException(asset, "Mod contained a loader from an unknown plugin: " + plugin);

			string loader = split[1];
			if (!convPlugin.Loaders.TryGetValue(loader, out string convLoader))
				throw NewUnconvertableException(asset, "Mod contained a loader that is no longer available: " + loader);

			return new YamlMappingNode
			{
				{
					"path", new YamlScalarNode(path)
					{
						Style = ScalarStyle.DoubleQuoted
					}
				},
				{
					"plugin", new YamlScalarNode(convPlugin.GUID)
					{
						Style = ScalarStyle.Plain
					}
				},
				{
					"loader", new YamlScalarNode(convLoader)
					{
						Style = ScalarStyle.Plain
					}
				}
			};
		}

		private YamlMappingNode GetDependencies(JObject dependencies)
		{
			return new()
			{
				{
					"hard", new YamlMappingNode(dependencies.Properties().Select(dependency =>
					{
						if (dependency is not {Name: { } plugin, Value: JValue {Value: string}})
							throw NewJsonException(dependency, "Invalid dependency");

						if (!_config.Plugins.TryGetValue(plugin, out Plugin convPlugin))
							throw NewUnconvertableException(dependency, "Unknown plugin: " + plugin);

						return new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(convPlugin.GUID),
							new YamlScalarNode(convPlugin.Version));
					}))
				}
			};
		}

		private YamlMappingNode GetAssets(JObject assets)
		{
			YamlMappingNode convAssets = new();

			if (assets["patcher"] is JObject {HasValues: true} patcher)
				throw NewUnconvertableException(patcher, "Mod contained patcher assets. Patcher assets are not supported in Stratum.");

			if (assets["setup"] is JObject setup)
				convAssets.Add("setup", new YamlSequenceNode(setup.Properties().Select(ConvertAsset).WhereNotNull()));

			if (assets["runtime"] is JObject runtime)
				convAssets.Add("runtime", new YamlMappingNode
				{
					{
						"nested", new YamlSequenceNode(runtime.Properties().Select(x => ConvertAsset(x) is { } asset
							? (YamlNode) new YamlMappingNode
							{
								{
									"assets", new YamlSequenceNode
									{
										asset
									}
								}
							}
							: null
						).WhereNotNull())
					}
				});

			return convAssets;
		}

		private YamlDocument GetDocument(JObject manifest)
		{
			YamlMappingNode root = new()
			{
				{
					"version", "1"
				}
			};

			if (manifest["dependencies"] is JObject dependencies)
				root.Add("dependencies", GetDependencies(dependencies));

			if (manifest["assets"] is JObject assets)
				root.Add("assets", GetAssets(assets));

			return new YamlDocument(root);
		}

		private void WriteProject(string directory, JObject manifest)
		{
			{
				YamlStream project = new(GetDocument(manifest));

				using StreamWriter text = new(Path.Combine(directory, "project.yaml"), false, Utility.UTF8NoBom);

				project.Save(text, false);
			}
		}

		private void WriteConfig(string directory)
		{
			using StreamWriter text = new(Path.Combine(directory, "config.yaml"), false, Utility.UTF8NoBom);

			_masonConfig.Save(text, false);
		}

		private void Cleanup(string path, string directory, string resources)
		{
			// So we don't run again next launch
			// Don't delete the file to prevent someone who spent their lifetime on a mod but didn't make any backups from getting pissed
			File.Move(path, Path.ChangeExtension(path, "delite_this"));

			const string manifestName = "manifest.json";
			File.Delete(Path.Combine(resources, manifestName));

			using IEnumerator<string> files = ((IEnumerable<string>) Directory.GetFiles(resources, manifestName, SearchOption.AllDirectories)).GetEnumerator();
			if (!files.MoveNext())
				return;

			string name = Path.GetFileName(directory);

			do
			{
				string manifest = files.Current!;

				File.Delete(manifest);
				_logger.LogWarning($"Deleted non-Deli manifest from '{name}' to avoid Deli crashing: '{manifest}'");
			} while (files.MoveNext());
		}

		private void Convert(string path)
		{
			string directory = Path.GetDirectoryName(path)!;

			string resources = Path.Combine(directory, "resources");
			if (Directory.Exists(resources))
				throw new IOException("Resources directory is already in use");

			using (ZipFile zip = ZipFile.Read(path))
			{
				const string manifestName = "manifest.json";
				if (zip[manifestName] is not { } entry)
					throw new InvalidOperationException("Mod contained no " + manifestName);

				JObject manifest = ReadManifest(entry);
				CheckForDeliDlls(zip);
				WriteProject(directory, manifest);
				WriteConfig(directory);
				ExtractResources(resources, zip);
			}

			Cleanup(path, directory, resources);
		}

		public void PreCompile(string directory)
		{
			// Ensure it is a TS package
			if (!File.Exists(Path.Combine(directory, "manifest.json")))
				return;

			// Ignore package if the name is listed as ignorable
			string name = Path.GetFileName(directory);
			string[] split = name.Split('-');
			if (split.Length == 2 && _config.Ignore.Contains(split[1]))
			{
				_logger.LogDebug($"Ignoring '{name}' because it is in the ignore filter");
				return;
			}

			using IEnumerator<string> mods = ((IEnumerable<string>) Directory.GetFiles(directory, "*.deli", SearchOption.AllDirectories))
				.GetEnumerator();

			// No mods
			if (!mods.MoveNext())
				return;

			string mod = mods.Current!;

			if (mods.MoveNext())
			{
				_logger.LogWarning($"'{name}' contained multiple .deli files. Skipping");
				return;
			}

			try
			{
				Convert(mod);
			}
			catch (UnconvertableException e)
			{
				_logger.LogWarning($"'{name}' is unconvertable. This is OK for mods that use loaders not yet ported to Stratum:\n{e.InnerException}");
			}
			catch (Exception e)
			{
				_logger.LogError($"'{name}' has an error in its format:\n{e}");
			}

			_logger.LogInfo($"Converted '{name}' to a Mason project");
		}
	}
}
