namespace StockSharp.Algo.Commissions
{
	using System;

	using StockSharp.Messages;

	/// <summary>
	/// The message adapter, automatically calculating commission.
	/// </summary>
	public class CommissionMessageAdapter : MessageAdapterWrapper
	{
		/// <summary>
		/// Initializes a new instance of the <see cref="CommissionMessageAdapter"/>.
		/// </summary>
		/// <param name="innerAdapter">The adapter, to which messages will be directed.</param>
		public CommissionMessageAdapter(IMessageAdapter innerAdapter)
			: base(innerAdapter)
		{
		}

		private ICommissionManager _commissionManager = new CommissionManager();

		/// <summary>
		/// The commission calculating manager.
		/// </summary>
		public ICommissionManager CommissionManager
		{
			get { return _commissionManager; }
			set
			{
				if (value == null)
					throw new ArgumentNullException("value");

				_commissionManager = value;
			}
		}

		/// <summary>
		/// Send message.
		/// </summary>
		/// <param name="message">Message.</param>
		public override void SendInMessage(Message message)
		{
			CommissionManager.Process(message);
			base.SendInMessage(message);
		}

		/// <summary>
		/// Process <see cref="MessageAdapterWrapper.InnerAdapter"/> output message.
		/// </summary>
		/// <param name="message">The message.</param>
		protected override void OnInnerAdapterNewOutMessage(Message message)
		{
			var execMsg = message as ExecutionMessage;

			if (execMsg != null && (execMsg.ExecutionType == ExecutionTypes.Order || execMsg.ExecutionType == ExecutionTypes.Trade) && execMsg.Commission == null)
				execMsg.Commission = CommissionManager.Process(execMsg);

			base.OnInnerAdapterNewOutMessage(message);
		}

		/// <summary>
		/// Create a copy of <see cref="CommissionMessageAdapter"/>.
		/// </summary>
		/// <returns>Copy.</returns>
		public override IMessageChannel Clone()
		{
			return new CommissionMessageAdapter((IMessageAdapter)InnerAdapter.Clone());
		}
	}
}