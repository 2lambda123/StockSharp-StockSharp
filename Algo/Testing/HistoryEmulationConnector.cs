namespace StockSharp.Algo.Testing
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	using Ecng.Collections;
	using Ecng.Common;
	using Ecng.ComponentModel;

	using MoreLinq;

	using StockSharp.Logging;
	using StockSharp.BusinessEntities;
	using StockSharp.Algo.Storages;
	using StockSharp.Algo.Candles;
	using StockSharp.Messages;
	using StockSharp.Localization;

	/// <summary>
	/// The emulational connection. It uses historic data and/or occasionally generated.
	/// </summary>
	public class HistoryEmulationConnector : BaseEmulationConnector, IExternalCandleSource
	{
		private class EmulationEntityFactory : EntityFactory
		{
			private readonly ISecurityProvider _securityProvider;
			private readonly IDictionary<string, Portfolio> _portfolios;

			public EmulationEntityFactory(ISecurityProvider securityProvider, IEnumerable<Portfolio> portfolios)
			{
				_securityProvider = securityProvider;
				_portfolios = portfolios.ToDictionary(p => p.Name, p => p, StringComparer.InvariantCultureIgnoreCase);
			}

			public override Security CreateSecurity(string id)
			{
				return _securityProvider.LookupById(id) ?? base.CreateSecurity(id);
			}

			public override Portfolio CreatePortfolio(string name)
			{
				return _portfolios.TryGetValue(name) ?? base.CreatePortfolio(name);
			}
		}

		private class HistoryBasketMessageAdapter : BasketMessageAdapter
		{
			private readonly HistoryEmulationConnector _parent;

			public HistoryBasketMessageAdapter(HistoryEmulationConnector parent)
				: base(parent.TransactionIdGenerator)
			{
				_parent = parent;
			}

			public override DateTimeOffset CurrentTime
			{
				get { return _parent.CurrentTime; }
			}
		}

		private readonly CachedSynchronizedDictionary<Tuple<SecurityId, MarketDataTypes, object>, int> _subscribedCandles = new CachedSynchronizedDictionary<Tuple<SecurityId, MarketDataTypes, object>, int>();
		private readonly CachedSynchronizedDictionary<Tuple<SecurityId, MarketDataTypes, object>, int> _historySourceSubscriptions = new CachedSynchronizedDictionary<Tuple<SecurityId, MarketDataTypes, object>, int>();
		private readonly SynchronizedDictionary<Tuple<SecurityId, MarketDataTypes, object>, CandleSeries> _series = new SynchronizedDictionary<Tuple<SecurityId, MarketDataTypes, object>, CandleSeries>();
		
		private readonly InMemoryMessageChannel _historyChannel;

		/// <summary>
		/// Initializes a new instance of the <see cref="HistoryEmulationConnector"/>.
		/// </summary>
		/// <param name="securities">Instruments, which will be sent through the <see cref="IConnector.NewSecurities"/> event.</param>
		/// <param name="portfolios">Portfolios, which will be sent through the <see cref="IConnector.NewPortfolios"/> event.</param>
		public HistoryEmulationConnector(IEnumerable<Security> securities, IEnumerable<Portfolio> portfolios)
			: this(securities, portfolios, new StorageRegistry())
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="HistoryEmulationConnector"/>.
		/// </summary>
		/// <param name="securities">Instruments, the operation will be performed with.</param>
		/// <param name="portfolios">Portfolios, the operation will be performed with.</param>
		/// <param name="storageRegistry">Market data storage.</param>
		public HistoryEmulationConnector(IEnumerable<Security> securities, IEnumerable<Portfolio> portfolios, IStorageRegistry storageRegistry)
			: this(new CollectionSecurityProvider(securities), portfolios, storageRegistry)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="HistoryEmulationConnector"/>.
		/// </summary>
		/// <param name="securityProvider">The provider of information about instruments.</param>
		/// <param name="portfolios">Portfolios, the operation will be performed with.</param>
		/// <param name="storageRegistry">Market data storage.</param>
		public HistoryEmulationConnector(ISecurityProvider securityProvider, IEnumerable<Portfolio> portfolios, IStorageRegistry storageRegistry)
		{
			if (securityProvider == null)
				throw new ArgumentNullException("securityProvider");

			if (portfolios == null)
				throw new ArgumentNullException("portfolios");

			if (storageRegistry == null)
				throw new ArgumentNullException("storageRegistry");

			// чтобы каждый раз при повторной эмуляции получать одинаковые номера транзакций
			TransactionIdGenerator = new IncrementalIdGenerator();

			_initialMoney = portfolios.ToDictionary(pf => pf, pf => pf.BeginValue);
			EntityFactory = new EmulationEntityFactory(securityProvider, _initialMoney.Keys);

			InMessageChannel = new PassThroughMessageChannel();
			OutMessageChannel = new PassThroughMessageChannel();

			LatencyManager = null;
			RiskManager = null;
			CommissionManager = null;
			PnLManager = null;
			SlippageManager = null;

			_historyAdapter = new HistoryMessageAdapter(TransactionIdGenerator, securityProvider) { StorageRegistry = storageRegistry };
			_historyChannel = new InMemoryMessageChannel("History Out", SendOutError);

			Adapter = new HistoryBasketMessageAdapter(this);
			Adapter.InnerAdapters.Add(EmulationAdapter);
			Adapter.InnerAdapters.Add(new ChannelMessageAdapter(_historyAdapter, new InMemoryMessageChannel("History In", SendOutError), _historyChannel));

			// при тестировании по свечкам, время меняется быстрее и таймаут должен быть больше 30с.
			ReConnectionSettings.TimeOutInterval = TimeSpan.MaxValue;

			MaxMessageCount = 1000;

			TradesKeepCount = 0;
		}

		private readonly HistoryMessageAdapter _historyAdapter;

		/// <summary>
		/// The adapter, receiving messages form the storage <see cref="IStorageRegistry"/>.
		/// </summary>
		public HistoryMessageAdapter HistoryMessageAdapter
		{
			get { return _historyAdapter; }
		}

		///// <summary>
		///// Интервал генерации сообщения <see cref="TimeMessage"/>. По-умолчанию равно 10 миллисекундам.
		///// </summary>
		//public override TimeSpan MarketTimeChangedInterval
		//{
		//	get { return _historyAdapter.MarketTimeChangedInterval; }
		//	set { _historyAdapter.MarketTimeChangedInterval = value; }
		//}

		///// <summary>
		///// Дата в истории, с которой необходимо начать эмуляцию.
		///// </summary>
		//public DateTimeOffset StartDate
		//{
		//	get { return _historyAdapter.StartDate; }
		//	set { _historyAdapter.StartDate = value; }
		//}

		///// <summary>
		///// Дата в истории, на которой необходимо закончить эмуляцию (дата включается).
		///// </summary>
		//public DateTimeOffset StopDate
		//{
		//	get { return _historyAdapter.StopDate; }
		//	set { _historyAdapter.StopDate = value; }
		//}

		/// <summary>
		/// The maximal size of the message queue, up to which history data are red. By default, it is equal to 1000.
		/// </summary>
		public int MaxMessageCount
		{
			get { return _historyChannel.MaxMessageCount; }
			set { _historyChannel.MaxMessageCount = value; }
		}

		private readonly Dictionary<Portfolio, decimal> _initialMoney;

		/// <summary>
		/// The initial size of monetary funds on accounts.
		/// </summary>
		public IDictionary<Portfolio, decimal> InitialMoney
		{
			get { return _initialMoney; }
		}

		/// <summary>
		/// The number of loaded messages.
		/// </summary>
		public int LoadedMessageCount { get { return _historyAdapter.LoadedMessageCount; } }

		/// <summary>
		/// The number of processed messages.
		/// </summary>
		public int ProcessedMessageCount { get { return EmulationAdapter.ProcessedMessageCount; } }

		private EmulationStates _state = EmulationStates.Stopped;

		/// <summary>
		/// The emulator state.
		/// </summary>
		public EmulationStates State
		{
			get { return _state; }
			private set
			{
				if (_state == value)
					return;

				bool throwError;

				switch (value)
				{
					case EmulationStates.Stopped:
						throwError = (_state != EmulationStates.Stopping);
						break;
					case EmulationStates.Stopping:
						throwError = (_state != EmulationStates.Started && _state != EmulationStates.Suspended
							&& State == EmulationStates.Starting);  // при ошибках при запуске эмуляции состояние может быть Starting
						break;
					case EmulationStates.Starting:
						throwError = (_state != EmulationStates.Stopped && _state != EmulationStates.Suspended);
						break;
					case EmulationStates.Started:
						throwError = (_state != EmulationStates.Starting);
						break;
					case EmulationStates.Suspending:
						throwError = (_state != EmulationStates.Started);
						break;
					case EmulationStates.Suspended:
						throwError = (_state != EmulationStates.Suspending);
						break;
					default:
						throw new ArgumentOutOfRangeException("value");
				}

				if (throwError)
					throw new InvalidOperationException(LocalizedStrings.Str2189Params.Put(_state, value));

				_state = value;

				try
				{
					StateChanged.SafeInvoke();
				}
				catch (Exception ex)
				{
					SendOutError(ex);
				}
			}
		}

		/// <summary>
		/// The event on the emulator state change <see cref="HistoryEmulationConnector.State"/>.
		/// </summary>
		public event Action StateChanged;

		///// <summary>
		///// Хранилище данных.
		///// </summary>
		//public IStorageRegistry StorageRegistry
		//{
		//	get { return _historyAdapter.StorageRegistry; }
		//	set { _historyAdapter.StorageRegistry = value; }
		//}

		///// <summary>
		///// Хранилище, которое используется по-умолчанию. По умолчанию используется <see cref="IStorageRegistry.DefaultDrive"/>.
		///// </summary>
		//public IMarketDataDrive Drive
		//{
		//	get { return _historyAdapter.Drive; }
		//	set { _historyAdapter.Drive = value; }
		//}

		///// <summary>
		///// Формат маркет-данных. По умолчанию используется <see cref="StorageFormats.Binary"/>.
		///// </summary>
		//public StorageFormats StorageFormat
		//{
		//	get { return _historyAdapter.StorageFormat; }
		//	set { _historyAdapter.StorageFormat = value; }
		//}

		/// <summary>
		/// Has the emulator ended its operation due to end of data, or it was interrupted through the <see cref="IConnector.Disconnect"/>method.
		/// </summary>
		public bool IsFinished { get; private set; }

		/// <summary>
		/// To enable the possibility to give out candles directly into <see cref="ICandleManager"/>. It accelerates operation, but candle change events will not be available. By default it is disabled.
		/// </summary>
		public bool UseExternalCandleSource { get; set; }

		/// <summary>
		/// Clear cache.
		/// </summary>
		public override void ClearCache()
		{
			base.ClearCache();
			IsFinished = false;
		}

		/// <summary>
		/// To call the <see cref="Connector.Connected"/> event when the first adapter connects to <see cref="Connector.Adapter"/>.
		/// </summary>
		protected override bool RaiseConnectedOnFirstAdapter
		{
			get { return false; }
		}

		///// <summary>
		///// Подключиться к торговой системе.
		///// </summary>
		//protected override void OnConnect()
		//{
		//	//SendInMessage(new TimeMessage { LocalTime = StartDate.LocalDateTime });
		//	SendInMessage(new ConnectMessage { LocalTime = StartDate.LocalDateTime });
		//}

		/// <summary>
		/// Disconnect from trading system.
		/// </summary>
		protected override void OnDisconnect()
		{
			SendEmulationState(EmulationStates.Stopping);
		}

		/// <summary>
		/// To start the emulation.
		/// </summary>
		public void Start()
		{
			SendEmulationState(EmulationStates.Starting);
		}

		/// <summary>
		/// To suspend the emulation.
		/// </summary>
		public void Suspend()
		{
			SendEmulationState(EmulationStates.Suspending);
		}

		private void SendEmulationState(EmulationStates state)
		{
			SendInMessage(new EmulationStateMessage { State = state });
		}

		/// <summary>
		/// To process the message, containing market data.
		/// </summary>
		/// <param name="message">The message, containing market data.</param>
		protected override void OnProcessMessage(Message message)
		{
			try
			{
				switch (message.Type)
				{
					case MessageTypes.Connect:
					{
						base.OnProcessMessage(message);

						if (message.Adapter == TransactionAdapter)
							_initialMoney.ForEach(p => SendPortfolio(p.Key));

						break;
					}

					case ExtendedMessageTypes.Last:
					{
						var lastMsg = (LastMessage)message;

						if (State == EmulationStates.Started)
						{
							IsFinished = !lastMsg.IsError;

							// все данных пришли без ошибок или в процессе чтения произошла ошибка - начинаем остановку
							SendEmulationState(EmulationStates.Stopping);
							SendEmulationState(EmulationStates.Stopped);
						}

						if (State == EmulationStates.Stopping)
						{
							// тестирование было отменено и пришли все ранее прочитанные данные
							SendEmulationState(EmulationStates.Stopped);
						}

						break;
					}

					case ExtendedMessageTypes.Clearing:
						break;

					case ExtendedMessageTypes.EmulationState:
						ProcessEmulationStateMessage(((EmulationStateMessage)message).State);
						break;

					case MessageTypes.Security:
					case MessageTypes.Board:
					case MessageTypes.Level1Change:
					case MessageTypes.QuoteChange:
					case MessageTypes.Time:
					case MessageTypes.CandlePnF:
					case MessageTypes.CandleRange:
					case MessageTypes.CandleRenko:
					case MessageTypes.CandleTick:
					case MessageTypes.CandleTimeFrame:
					case MessageTypes.CandleVolume:
					case MessageTypes.Execution:
					{
						if (message.Adapter == MarketDataAdapter)
							TransactionAdapter.SendInMessage(message);
						else if (message.Adapter == TransactionAdapter)
						{
							var candleMsg = message as CandleMessage;

							if (candleMsg != null)
							{
								if (!UseExternalCandleSource)
									break;

								var series = _series.TryGetValue(Tuple.Create(candleMsg.SecurityId, candleMsg.Type.ToCandleMarketDataType(), candleMsg.Arg));

								if (series != null)
								{
									_newCandles.SafeInvoke(series, new[] { candleMsg.ToCandle(series) });

									if (candleMsg.IsFinished)
										_stopped.SafeInvoke(series);
								}

								break;
							}
						}

						base.OnProcessMessage(message);
						break;
					}

					default:
					{
						if (State == EmulationStates.Stopping && message.Type != MessageTypes.Disconnect)
							break;

						base.OnProcessMessage(message);
						break;
					}
				}
			}
			catch (Exception ex)
			{
				SendOutError(ex);
				SendEmulationState(EmulationStates.Stopping);
			}
		}

		private void ProcessEmulationStateMessage(EmulationStates newState)
		{
			this.AddInfoLog(LocalizedStrings.Str1121Params, State, newState);

			State = newState;

			switch (newState)
			{
				case EmulationStates.Stopping:
				{
					SendInMessage(new DisconnectMessage());
					break;
				}

				case EmulationStates.Starting:
				{
					SendEmulationState(EmulationStates.Started);
					break;
				}
			}
		}

		private void SendPortfolio(Portfolio portfolio)
		{
			SendInMessage(portfolio.ToMessage());

			var money = _initialMoney[portfolio];

			SendInMessage(
				EmulationAdapter
					.CreatePortfolioChangeMessage(portfolio.Name)
						.Add(PositionChangeTypes.BeginValue, money)
						.Add(PositionChangeTypes.CurrentValue, money)
						.Add(PositionChangeTypes.BlockedValue, 0m));
		}

		//private void InitOrderLogBuilders(DateTime loadDate)
		//{
		//	if (StorageRegistry == null || !MarketEmulator.Settings.UseMarketDepth)
		//		return;

		//	foreach (var security in RegisteredMarketDepths)
		//	{
		//		var builder = _orderLogBuilders.TryGetValue(security);

		//		if (builder == null)
		//			continue;

		//		// стакан из ОЛ строиться начиная с 18.45 предыдущей торговой сессии
		//		var olDate = loadDate.Date;

		//		do
		//		{
		//			olDate -= TimeSpan.FromDays(1);
		//		}
		//		while (!ExchangeBoard.Forts.WorkingTime.IsTradeDate(olDate));

		//		olDate += new TimeSpan(18, 45, 0);

		//		foreach (var item in StorageRegistry.GetOrderLogStorage(security, Drive).Load(olDate, loadDate - TimeSpan.FromTicks(1)))
		//		{
		//			builder.Update(item);
		//		}
		//	}
		//}

		///// <summary>
		///// Найти инструменты, соответствующие фильтру <paramref name="criteria"/>.
		///// </summary>
		///// <param name="criteria">Инструмент, поля которого будут использоваться в качестве фильтра.</param>
		///// <returns>Найденные инструменты.</returns>
		//public override IEnumerable<Security> Lookup(Security criteria)
		//{
		//	var securities = _historyAdapter.SecurityProvider.Lookup(criteria);

		//	if (State == EmulationStates.Started)
		//	{
		//		foreach (var security in securities)
		//			SendSecurity(security);	
		//	}

		//	return securities;
		//}

		/// <summary>
		/// Subscribe on the portfolio changes.
		/// </summary>
		/// <param name="portfolio">Portfolio for subscription.</param>
		protected override void OnRegisterPortfolio(Portfolio portfolio)
		{
			_initialMoney.TryAdd(portfolio, portfolio.BeginValue);

			if (State == EmulationStates.Started)
				SendPortfolio(portfolio);
		}

		/// <summary>
		/// Зарегистрировать исторические данные.
		/// </summary>
		/// <param name="security">Инструмент.</param>
		/// <param name="dataType">Тип данных.</param>
		/// <param name="arg">Параметр, ассоциированный с типом <paramref name="dataType"/>. Например, <see cref="Candle.Arg"/>.</param>
		/// <param name="getMessages">Функция получения исторических данных.</param>
		public void RegisterHistorySource(Security security, MarketDataTypes dataType, object arg, Func<DateTimeOffset, IEnumerable<Message>> getMessages)
		{
			SendInHistorySourceMessage(security, dataType, arg, getMessages);
		}
		
		/// <summary>
		/// Удалить регистрацию, ранее осуществленную через <see cref="RegisterHistorySource"/>.
		/// </summary>
		/// <param name="security">Инструмент.</param>
		/// <param name="dataType">Тип данных.</param>
		/// <param name="arg">Параметр, ассоциированный с типом <paramref name="dataType"/>. Например, <see cref="Candle.Arg"/>.</param>
		public void UnRegisterHistorySource(Security security, MarketDataTypes dataType, object arg)
		{
			SendInHistorySourceMessage(security, dataType, arg, null);
		}

		private void SendInHistorySourceMessage(Security security, MarketDataTypes dataType, object arg, Func<DateTimeOffset, IEnumerable<Message>> getMessages)
		{
			var isSubscribe = getMessages != null;

			if (isSubscribe)
			{
				if (_historySourceSubscriptions.ChangeSubscribers(Tuple.Create(security.ToSecurityId(), dataType, arg), true) != 1)
					return;
			}
			else
			{
				if (_historySourceSubscriptions.ChangeSubscribers(Tuple.Create(security.ToSecurityId(), dataType, arg), false) != 0)
					return;
			}

			SendInMessage(new HistorySourceMessage
			{
				IsSubscribe = isSubscribe,
				SecurityId = security.ToSecurityId(),
				DataType = dataType,
				Arg = arg,
				GetMessages = getMessages
			});
		}

		IEnumerable<Range<DateTimeOffset>> IExternalCandleSource.GetSupportedRanges(CandleSeries series)
		{
			if (!UseExternalCandleSource)
				yield break;

			var securityId = series.Security.ToSecurityId();
			var dataType = series.CandleType.ToCandleMessageType().ToCandleMarketDataType();

			if (_historySourceSubscriptions.ContainsKey(Tuple.Create(securityId, dataType, series.Arg)))
			{
				yield return new Range<DateTimeOffset>(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
				yield break;
			}

			var types = _historyAdapter.Drive.GetCandleTypes(securityId, _historyAdapter.StorageFormat);

			foreach (var tuple in types)
			{
				if (tuple.Item1 != series.CandleType.ToCandleMessageType())
					continue;

				foreach (var arg in tuple.Item2)
				{
					if (!arg.Equals(series.Arg))
						continue;

					var dates = _historyAdapter.StorageRegistry.GetCandleMessageStorage(tuple.Item1, series.Security, arg, _historyAdapter.Drive, _historyAdapter.StorageFormat).Dates;

					if (dates.Any())
						yield return new Range<DateTimeOffset>(dates.First().ApplyTimeZone(TimeZoneInfo.Utc), dates.Last().ApplyTimeZone(TimeZoneInfo.Utc));

					break;
				}

				break;
			}
		}

		private Action<CandleSeries, IEnumerable<Candle>> _newCandles;

		event Action<CandleSeries, IEnumerable<Candle>> IExternalCandleSource.NewCandles
		{
			add { _newCandles += value; }
			remove { _newCandles -= value; }
		}

		private Action<CandleSeries> _stopped;

		event Action<CandleSeries> IExternalCandleSource.Stopped
		{
			add { _stopped += value; }
			remove { _stopped -= value; }
		}

		void IExternalCandleSource.SubscribeCandles(CandleSeries series, DateTimeOffset from, DateTimeOffset to)
		{
			var securityId = GetSecurityId(series.Security);
			var dataType = series.CandleType.ToCandleMessageType().ToCandleMarketDataType();
			var key = Tuple.Create(securityId, dataType, series.Arg);

			if (!_historySourceSubscriptions.ContainsKey(key))
			{
				if (_subscribedCandles.ChangeSubscribers(key, true) != 1)
					return;

				MarketDataAdapter.SendInMessage(new MarketDataMessage
				{
					//SecurityId = securityId,
					DataType = dataType,
					Arg = series.Arg,
					IsSubscribe = true,
				}.FillSecurityInfo(this, series.Security));
			}

			_series.Add(key, series);
		}

		void IExternalCandleSource.UnSubscribeCandles(CandleSeries series)
		{
			var securityId = GetSecurityId(series.Security);
			var dataType = series.CandleType.ToCandleMessageType().ToCandleMarketDataType();
			var key = Tuple.Create(securityId, dataType, series.Arg);

			if (!_historySourceSubscriptions.ContainsKey(key))
			{
				if (_subscribedCandles.ChangeSubscribers(key, false) != 0)
					return;

				MarketDataAdapter.SendInMessage(new MarketDataMessage
				{
					//SecurityId = securityId,
					DataType = MarketDataTypes.CandleTimeFrame,
					Arg = series.Arg,
					IsSubscribe = false,
				}.FillSecurityInfo(this, series.Security));
			}

			_series.Remove(key);
		}
	}
}