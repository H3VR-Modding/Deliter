using System.Collections.Generic;
using System.Linq;

namespace Deliter
{
	internal static class Extensions
	{
		public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> @this) where T : class
		{
			return @this.Where(x => x != null)!;
		}
	}
}
