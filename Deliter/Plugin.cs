using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using YamlDotNet.Serialization;

#pragma warning disable 8618

namespace Deliter
{
	[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
	[SuppressMessage("ReSharper", "CollectionNeverUpdated.Global")]
	[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
	internal class Plugin
	{
		[YamlMember(Alias = "guid")]
		public string GUID { get; set; }

		public string Version { get; set; }
		public Dictionary<string, string> Loaders { get; set; }
	}
}
