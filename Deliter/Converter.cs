using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using Ionic.Zip;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Deliter
{
	internal class Converter
	{
		private const string ManifestJson = "manifest.json";

		private static void Add(YamlNode existing, YamlNode additive)
		{
			static void Mismatch()
			{
				throw new ArgumentException("Existing and additive are not the same type");
			}

			switch (existing)
			{
				case YamlMappingNode lhs:
					switch (additive)
					{
						case YamlMappingNode rhs:
							Add(lhs, rhs);
							break;
						default:
							Mismatch();
							break;
					}
					break;

				case YamlSequenceNode lhs:
					switch (additive)
					{
						case YamlSequenceNode rhs:
							Add(lhs, rhs);
							break;
						default:
							Mismatch();
							break;
					}
					break;

				case YamlScalarNode lhs:
					switch (additive)
					{
						case YamlScalarNode rhs:
							lhs.Value = rhs.Value;
							break;
						default:
							Mismatch();
							break;
					}
					break;

				default:
					throw new ArgumentException("Unknown type", nameof(existing));
			}
		}

		private static void Add(YamlSequenceNode existing, YamlSequenceNode additive)
		{
			foreach (YamlNode node in additive)
				existing.Add(node);
		}

		private static void Add(YamlMappingNode existing, YamlMappingNode additive)
		{
			foreach (KeyValuePair<YamlNode, YamlNode> pair in additive)
			{
				YamlScalarNode key = (YamlScalarNode) pair.Key;
				YamlNode value = pair.Value;

				if (existing.FirstOrDefault(x => x.Key is YamlScalarNode scalar && scalar.Value == key.Value).Value is { } current)
					Add(current, value);
				else
					existing.Add(key, value);
			}
		}

		private static JsonSerializationException NewException(JToken token, string message)
		{
			IJsonLineInfo info = token;

			return new JsonSerializationException(message, token.Path, info.LineNumber, info.LinePosition, null);
		}

		private static JObject ReadManifest(ZipEntry entry)
		{
			using Stream raw = entry.OpenReader();
			using StreamReader text = new(raw);
			using JsonTextReader json = new(text);

			return JObject.Load(json);
		}

		private static void CreateAllDirectories(string path)
		{
			if (Path.GetDirectoryName(path) is { } parent && !Directory.Exists(parent))
				CreateAllDirectories(parent);

			Directory.CreateDirectory(path);
		}

		private void ExtractResources(string resources, ZipFile zip)
		{
			Directory.CreateDirectory(resources);
			try
			{
				foreach (ZipEntry entry in zip.Entries)
				{
					string name = entry.FileName;
					string path = Path.Combine(resources, name);

					if (entry.IsDirectory)
					{
						CreateAllDirectories(path);
					}
					else
					{
						void ExtractFile()
						{
							using FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None);

							entry.Extract(file);
						}

						if (Path.GetFileName(name) == ManifestJson)
						{
							bool isRootManifest = string.IsNullOrEmpty(Path.GetDirectoryName(name)?.Trim());

							if (isRootManifest)
							{
								// Ignore it. All Deli files have one
							}
							else
							{
								// This could break something, so warn
								LogWarning($"Ignoring non-Deli manifest from '{zip}' to avoid Deli crashing: '{name}'");
							}
						}
						else if (File.Exists(path))
						{
							if (File.GetLastWriteTime(path) < entry.LastModified)
								ExtractFile();
						}
						else
						{
							if (Path.GetDirectoryName(path) is { } parent)
								CreateAllDirectories(parent);

							ExtractFile();
						}
					}
				}
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
				throw NewException(asset, "Invalid asset");

			string[] split = rawLoader.Split(':');
			if (split.Length != 2)
				throw NewException(asset, "Loaders must be a mod GUID and loader name, separated by a single colon");

			if (split[0] == "deli" && split[1] == "assembly")
				// Ignore. This will be loaded by BepInEx, if applicable
				return null;

			string plugin = split[0];
			if (!_config.Plugins.TryGetValue(plugin, out Plugin convPlugin))
				// Mod contained a loader from an unknown plugin
				return null;

			string loader = split[1];
			if (!convPlugin.Loaders.TryGetValue(loader, out string convLoader))
				// Mod contained a loader that is no longer available
				return null;

			asset.Remove();

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
			YamlMappingNode mapping = new();

			foreach (JProperty property in dependencies.Properties().ToList())
			{
				if (property is not {Name: { } plugin, Value: JValue {Value: string}})
					throw NewException(property, "Invalid dependency");

				if (!_config.Plugins.TryGetValue(plugin, out Plugin convPlugin))
					// Unknown plugin
					continue;

				property.Remove();

				mapping.Add(new YamlScalarNode(convPlugin.GUID), new YamlScalarNode(convPlugin.Version));
			}

			return new YamlMappingNode
			{
				{
					"hard", mapping
				}
			};
		}

		private YamlMappingNode GetAssets(JObject assets)
		{
			YamlMappingNode convAssets = new();

			if (assets["patcher"] is JObject {HasValues: true} patcher)
				throw NewException(patcher, "Mod contained patcher assets. Patcher assets are not supported in Stratum.");

			if (assets["setup"] is JObject setup)
				convAssets.Add("setup", new YamlSequenceNode(setup.Properties().ToList().Select(ConvertAsset).WhereNotNull()));

			if (assets["runtime"] is JObject runtime)
				convAssets.Add("runtime", new YamlMappingNode
				{
					{
						"nested", new YamlSequenceNode(runtime.Properties().ToList().Select(ConvertAsset).WhereNotNull().Select(asset =>
							(YamlNode)new YamlMappingNode
							{
								{
									"assets", new YamlSequenceNode
									{
										asset
									}
								}
							}))
					}
				});

			return convAssets;
		}

		private YamlDocument GetDocument(JObject manifest, out bool partial)
		{
			partial = false;

			YamlMappingNode root = new()
			{
				{
					"version", "1"
				}
			};

			if (manifest["dependencies"] is JObject dependencies)
			{
				root.Add("dependencies", GetDependencies(dependencies));

				partial = partial || dependencies.Count != 0;
			}

			if (manifest["assets"] is JObject assets)
			{
				root.Add("assets", GetAssets(assets));

				partial = partial || assets.Count != 0;
			}

			return new YamlDocument(root);
		}

		private void WriteProject(string path, JObject manifest, out bool partial)
		{
			YamlStream project = new();

			if (File.Exists(path))
			{
				using StreamReader text = new(path);

				project.Load(text);
			}

			YamlDocument doc = GetDocument(manifest, out partial);

			if (project.Documents.FirstOrDefault() is { } existing)
				Add(existing.RootNode, doc.RootNode);
			else
				project.Add(doc);

			{
				using StreamWriter text = new(path, false, Utility.UTF8NoBom);

				project.Save(text, false);
			}
		}

		private void WriteConfig(string directory)
		{
			using StreamWriter text = new(Path.Combine(directory, "config.yaml"), false, Utility.UTF8NoBom);

			_masonConfig.Save(text, false);
		}

		private void Convert(string path, string name)
		{
			string directory = Path.GetDirectoryName(path)!;
			string project = Path.Combine(directory, "project.yaml");

			string resources = Path.Combine(directory, "resources");

			// Don't delete the file to prevent someone who spent their lifetime on a mod but didn't make any backups from getting pissed
			// Ignore if backup already exists because this might be a partially delited mod.
			string backupPath = path + ".bak";
			if (!File.Exists(backupPath))
				File.Copy(path, backupPath, true);

			bool partial;
			using (ZipFile zip = ZipFile.Read(path))
			{
				if (zip[ManifestJson] is not { } entry)
					throw new InvalidOperationException("Mod contained no " + ManifestJson);

				JObject manifest = ReadManifest(entry);
				WriteProject(project, manifest, out partial);
				WriteConfig(directory);
				ExtractResources(resources, zip);

				if (partial)
				{
					// Readjust manifest

					string entryName = entry.FileName;
					zip.RemoveEntry(entry);

					byte[] raw;
					{
						using MemoryStream memory = new();

						{
							using StreamWriter text = new(memory);
							using JsonTextWriter writer = new(text);

							manifest.WriteTo(writer);
						}

						raw = memory.ToArray();
					}

					zip.AddEntry(entryName, raw);

					zip.Save();
				}
			}

			if (!partial)
				// So we don't run again next launch
				File.Delete(path);

			LogInfo($"Converted '{name}' to a Mason project");
		}

		public void PreCompile(string directory)
		{
			// Ensure it is a TS package
			if (!File.Exists(Path.Combine(directory, ManifestJson)))
				return;

			// Ignore package if the name is listed as ignorable
			string name = Path.GetFileName(directory);
			string[] split = name.Split('-');
			if (split.Length == 2 && _config.Ignore.Contains(split[1]))
			{
				LogDebug($"Ignoring '{name}' because it is in the ignore filter");
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
				LogWarning($"'{name}' contained multiple .deli files. Skipping");
				return;
			}

			try
			{
				Convert(mod, name);
			}
			catch (JsonSerializationException e)
			{
				LogError($"At ({e.LineNumber}, {e.LinePosition}) ({e.Path}), {e}");
			}
			catch (Exception e)
			{
				LogError($"'{name}' has an error in its format:\n{e}");
			}
		}

		private void LogInfo(object obj)
		{
			lock (_logger)
			{
				_logger.LogInfo(obj);
			}
		}

		private void LogWarning(object obj)
		{
			lock (_logger)
			{
				_logger.LogWarning(obj);
			}
		}

		private void LogError(object obj)
		{
			lock (_logger)
			{
				_logger.LogError(obj);
			}
		}

		private void LogDebug(object obj)
		{
			lock (_logger)
			{
				_logger.LogDebug(obj);
			}
		}
	}
}
