
using System;

namespace Lidgren.Network
{
	/// <summary>
	/// How the library deals with resends and handling of late messages.
	/// </summary>
	public enum NetDeliveryMethod : byte
	{
		// Publicly visible subset of NetMessageType

		/// <summary>
		/// Indicates an error.
		/// </summary>
		Unknown = 0,

		/// <summary>
		/// Unreliable, unordered delivery.
		/// </summary>
		Unreliable = NetMessageType.UserUnreliable,

		/// <summary>
		/// Unreliable delivery, but automatically dropping late messages.
		/// </summary>
		UnreliableSequenced = NetMessageType.UserSequenced1,

		/// <summary>
		/// Reliable delivery, but unordered.
		/// </summary>
		ReliableUnordered = NetMessageType.UserReliableUnordered,

		/// <summary>
		/// Reliable delivery, except for late messages which are dropped.
		/// </summary>
		ReliableSequenced = NetMessageType.UserReliableSequenced1,

		/// <summary>
		/// Reliable, ordered delivery.
		/// </summary>
		ReliableOrdered = NetMessageType.UserReliableOrdered1,
	}
}
