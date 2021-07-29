using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

#pragma warning disable 8618

namespace Deliter
{
	[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
	[SuppressMessage("ReSharper", "CollectionNeverUpdated.Global")]
	[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
	internal class Config
	{
		public HashSet<string> Ignore { get; set; }
		public Dictionary<string, Plugin> Plugins { get; set; }
	}
}
