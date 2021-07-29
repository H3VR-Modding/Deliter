using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Deliter
{
	internal class Converter
	{
		private readonly Config _config;

		public Converter(Config config)
		{
			_config = config;
		}

		private static JsonSerializationException NewException(JToken token, string message)
		{
			IJsonLineInfo info = token;

			return new JsonSerializationException(message, token.Path, info.LineNumber, info.LinePosition, null);
		}

		private YamlNode? ConvertAsset(JProperty asset)
		{
			if (asset is not {Name: { } path, Value: JValue {Value: string rawLoader}})
				throw NewException(asset, "Invalid asset");

			string[] split = rawLoader.Split(':');
			if (split.Length != 2)
				throw NewException(asset, "Loaders must be a mod GUID and loader name, separated by a single colon");

			// Ignore. This will be loaded by BepInEx, if applicable
			if (split[0] == "deli" && split[1] == "assembly")
				return null;

			string plugin = split[0];
			if (!_config.Plugins.TryGetValue(plugin, out Plugin convPlugin))
				throw NewException(asset, "Mod contained a loader from an unknown plugin: " + plugin);

			string loader = split[1];
			if (!convPlugin.Loaders.TryGetValue(loader, out string convLoader))
				throw NewException(asset, "Mod contained a loader that is no longer available: " + loader);

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
							throw NewException(dependency, "Invalid dependency");

						if (!_config.Plugins.TryGetValue(plugin, out Plugin convPlugin))
							throw NewException(dependency, "Unknown plugin: " + convPlugin);

						return new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(convPlugin.GUID), new YamlScalarNode(convPlugin.Version));
					}))
				}
			};
		}

		private YamlMappingNode GetAssets(JObject assets)
		{
			YamlMappingNode convAssets = new();

			if (assets["patcher"] is JObject {HasValues: true} patcher)
				throw NewException(patcher, "Mod contained patcher assets. Patcher assets are not supported in Stratum.");

			if (assets["setup"] is JObject setup)
				convAssets.Add("setup", new YamlSequenceNode(setup.Properties().Select(ConvertAsset).WhereNotNull()));

			if (assets["runtime"] is JObject runtime)
				convAssets.Add("runtime", new YamlMappingNode
				{
					{
						"nested",
						new YamlSequenceNode(runtime.Properties().Select(x => ConvertAsset(x) is { } asset
							? (YamlNode) new YamlMappingNode
							{
								{
									"assets", new YamlSequenceNode()
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
				{ "version", "1" }
			};

			if (manifest["dependencies"] is JObject dependencies)
				root.Add("dependencies", GetDependencies(dependencies));

			if (manifest["assets"] is JObject assets)
				root.Add("assets", GetAssets(assets));

			return new YamlDocument(root);
		}

		public YamlStream Convert(JObject manifest)
		{
			return new(GetDocument(manifest));
		}
	}
}
