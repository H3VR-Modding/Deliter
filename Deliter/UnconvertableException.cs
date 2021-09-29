using System;

namespace Deliter
{
	public class UnconvertableException : Exception
	{
		public UnconvertableException(Exception e) : base(null, e) { }
	}
}
