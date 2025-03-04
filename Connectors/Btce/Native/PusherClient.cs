namespace StockSharp.Btce.Native;

using Ecng.ComponentModel;

class PusherClient : BaseLogReceiver
{
	// to get readable name after obfuscation
	public override string Name => nameof(Btce) + "_" + nameof(PusherClient);

	public event Action<string, PusherTransaction[]> NewTrades;
	public event Action<string, OrderBook> OrderBookChanged;
	public event Action<Exception> Error;
	public event Action<ConnectionStates> StateChanged;

	private readonly WebSocketClient _client;

	public PusherClient()
	{
		_client = new WebSocketClient(
			state => StateChanged?.Invoke(state),
			error =>
			{
				this.AddErrorLog(error);
				Error?.Invoke(error);
			},
			OnProcess,
			(s, a) => this.AddInfoLog(s, a),
			(s, a) => this.AddErrorLog(s, a),
			(s, a) => this.AddVerboseLog(s, a));
	}

	protected override void DisposeManaged()
	{
		_client.Dispose();
		base.DisposeManaged();
	}

	public ValueTask Connect(CancellationToken cancellationToken)
	{
		this.AddInfoLog(LocalizedStrings.Connecting);
		return _client.ConnectAsync("wss://ws-eu.pusher.com/app/ee987526a24ba107824c?client=stocksharp&version=1.0&protocol=7", cancellationToken: cancellationToken);
	}

	public void Disconnect()
	{
		this.AddInfoLog(LocalizedStrings.Disconnecting);
		_client.Disconnect();
	}

	private ValueTask OnProcess(WebSocketMessage msg, CancellationToken cancellationToken)
	{
		var obj = msg.AsObject();
		var evt = (string)obj.@event;

		switch (evt)
		{
			case "pusher_internal:subscription_succeeded":
				break;

			case "connection_established":
				break;

			case Channels.OrderBook:
				OrderBookChanged?.Invoke(((string)obj.channel).Remove("." + Channels.OrderBook), ((string)obj.data).DeserializeObject<OrderBook>());
				break;

			case Channels.Trades:
				NewTrades?.Invoke(((string)obj.channel).Remove("." + Channels.Trades), ((string)obj.data).DeserializeObject<PusherTransaction[]>());
				break;

			default:
				this.AddErrorLog(LocalizedStrings.UnknownEvent, evt);
				break;
		}

		return default;
	}

	private static class Channels
	{
		public const string Trades = "trades";
		public const string OrderBook = "depth";
	}

	public ValueTask SubscribeTrades(string currency, CancellationToken cancellationToken)
	{
		return Process("subscribe", Channels.Trades, currency, cancellationToken);
	}

	public ValueTask UnSubscribeTrades(string currency, CancellationToken cancellationToken)
	{
		return Process("unsubscribe", Channels.Trades, currency, cancellationToken);
	}

	public ValueTask SubscribeOrderBook(string currency, CancellationToken cancellationToken)
	{
		return Process("subscribe", Channels.OrderBook, currency, cancellationToken);
	}

	public ValueTask UnSubscribeOrderBook(string currency, CancellationToken cancellationToken)
	{
		return Process("unsubscribe", Channels.OrderBook, currency, cancellationToken);
	}

	private ValueTask Process(string action, string channel, string currency, CancellationToken cancellationToken)
	{
		if (action.IsEmpty())
			throw new ArgumentNullException(nameof(action));

		if (currency.IsEmpty())
			throw new ArgumentNullException(nameof(currency));

		if (channel.IsEmpty())
			throw new ArgumentNullException(nameof(channel));

		return _client.SendAsync(new
		{
			@event = "pusher:" + action,
			data = new
			{
				channel = currency + "." + channel,
			},
		}, cancellationToken);
	}
}