using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lidgren.Network
{
	/// <summary>
	/// Exception commonly thrown by the Lidgren Network Library.
	/// </summary>
	public class LidgrenException : Exception
	{
		public LidgrenException() : base()
		{
		}

		public LidgrenException(string? message) : base(message)
		{
		}

		public LidgrenException(string? message, Exception? innerException) : base(message, innerException)
		{
		}

		/// <summary>
		/// Throws an exception, in DEBUG only, if first parameter is false.
		/// </summary>
		[Conditional("DEBUG")]
		public static void Assert([DoesNotReturnIf(false)] bool condition, string? message)
		{
			if (!condition)
				throw new LidgrenException(message);
		}

		/// <summary>
		/// Throws an exception, in DEBUG only, if first parameter is false.
		/// </summary>
		[Conditional("DEBUG")]
		public static void Assert([DoesNotReturnIf(false)] bool condition)
		{
			if (!condition)
				throw new LidgrenException();
		}
	}
}
