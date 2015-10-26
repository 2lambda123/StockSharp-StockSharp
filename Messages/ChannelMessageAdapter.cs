namespace StockSharp.Messages
{
	using System;

	/// <summary>
	/// Message adapter, forward messages through a transport channel <see cref="IMessageChannel"/>.
	/// </summary>
	public class ChannelMessageAdapter : MessageAdapterWrapper
	{
		/// <summary>
		/// Initializes a new instance of the <see cref="ChannelMessageAdapter"/>.
		/// </summary>
		/// <param name="innerAdapter">Underlying adapter.</param>
		/// <param name="inputChannel">Incomming messages channgel.</param>
		/// <param name="outputChannel">Outgoing message channel.</param>
		public ChannelMessageAdapter(IMessageAdapter innerAdapter, IMessageChannel inputChannel, IMessageChannel outputChannel)
			: base(innerAdapter)
		{
			if (inputChannel == null)
				throw new ArgumentNullException("inputChannel");

			if (outputChannel == null)
				throw new ArgumentNullException("outputChannel");

			InputChannel = inputChannel;
			OutputChannel = outputChannel;

			InputChannel.NewOutMessage += InputChannelOnNewOutMessage;
			OutputChannel.NewOutMessage += OutputChannelOnNewOutMessage;
		}

		/// <summary>
		/// Adapter.
		/// </summary>
		public IMessageChannel InputChannel { get; private set; }

		/// <summary>
		/// Adapter.
		/// </summary>
		public IMessageChannel OutputChannel { get; private set; }

		/// <summary>
		/// Control the lifetime of the incoming messages channel.
		/// </summary>
		public bool OwnInputChannel { get; set; }

		/// <summary>
		/// Control the lifetime of the outgoing messages channel.
		/// </summary>
		public bool OwnOutputChannel { get; set; }

		private void OutputChannelOnNewOutMessage(Message message)
		{
			RaiseNewOutMessage(message);
		}

		/// <summary>
		/// Process <see cref="MessageAdapterWrapper.InnerAdapter"/> output message.
		/// </summary>
		/// <param name="message">The message.</param>
		protected override void OnInnerAdapterNewOutMessage(Message message)
		{
			if (!OutputChannel.IsOpened)
				OutputChannel.Open();

			OutputChannel.SendInMessage(message);
		}

		private void InputChannelOnNewOutMessage(Message message)
		{
			InnerAdapter.SendInMessage(message);
		}

		/// <summary>
		/// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
		/// </summary>
		public override void Dispose()
		{
			InputChannel.NewOutMessage -= InputChannelOnNewOutMessage;
			OutputChannel.NewOutMessage -= OutputChannelOnNewOutMessage;

			if (OwnInputChannel)
				InputChannel.Dispose();

			if (OwnOutputChannel)
				OutputChannel.Dispose();

			base.Dispose();
		}

		/// <summary>
		/// Send message.
		/// </summary>
		/// <param name="message">Message.</param>
		public override void SendInMessage(Message message)
		{
			if (!InputChannel.IsOpened)
				InputChannel.Open();

			InputChannel.SendInMessage(message);
		}

		/// <summary>
		/// Create a copy of <see cref="ChannelMessageAdapter"/>.
		/// </summary>
		/// <returns>Copy.</returns>
		public override IMessageChannel Clone()
		{
			return new ChannelMessageAdapter(InnerAdapter, InputChannel.Clone(), OutputChannel.Clone());
		}
	}
}