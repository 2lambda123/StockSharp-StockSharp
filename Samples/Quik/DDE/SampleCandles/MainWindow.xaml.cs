namespace SampleCandles
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Windows;
    using System.Windows.Controls;

    using Ookii.Dialogs.Wpf;

    using Ecng.Common;
    using Ecng.Collections;
    using Ecng.Xaml;

    using StockSharp.Algo.Candles;
    using StockSharp.BusinessEntities;
    using StockSharp.Messages;
    using StockSharp.Quik;
    using StockSharp.Xaml.Charting;
    using StockSharp.Localization;
    using StockSharp.Logging;

    partial class MainWindow
    {
        private readonly Dictionary<CandleSeries, ChartWindow> _chartWindows = new Dictionary<CandleSeries, ChartWindow>();
        private QuikTrader _trader;
        private bool _isDdeStarted;
        private CandleManager _candleManager;
        private LogManager logManager;

        public MainWindow()
        {
            InitializeComponent();
            CandleType.SetDataSource<CandleTypes>();
            CandleType.SetSelectedValue<CandleTypes>(CandleTypes.TimeFrame);

            TimeFrame.Value = new DateTime(TimeSpan.FromMinutes(5).Ticks);

            // попробовать сразу найти месторасположение Quik по запущенному процессу
            Path.Text = QuikTerminal.GetDefaultPath();

            //Добавим логирование
            logManager = new LogManager
            {
                Application = { LogLevel = LogLevels.Debug }
            };

            logManager.Listeners.Add(new FileLogListener
            {
                LogDirectory = @"Logs\",
                SeparateByDates = SeparateByDateModes.SubDirectories,
                Append = false,
            });
        }

        private void FindPathClick(object sender, RoutedEventArgs e)
        {
            var dlg = new VistaFolderBrowserDialog();

            if (!Path.Text.IsEmpty())
                dlg.SelectedPath = Path.Text;

            if (dlg.ShowDialog(this) == true)
            {
                Path.Text = dlg.SelectedPath;
            }
        }

        private void ConnectClick(object sender, RoutedEventArgs e)
        {
            if (Path.Text.IsEmpty())
                MessageBox.Show(this, LocalizedStrings.Str2983);
            else
            {
                _trader = new QuikTrader(Path.Text) { IsDde = isDDE.IsChecked == true };
                logManager.Sources.Add(_trader);
                // подписываемся на событие об успешном восстановлении соединения
                _trader.ReConnectionSettings.ConnectionSettings.Restored += () => this.GuiAsync(() => MessageBox.Show(this, LocalizedStrings.Str2958));

                // подписываемся на событие разрыва соединения
                _trader.ConnectionError += error => this.GuiAsync(() => MessageBox.Show(this, error.ToString()));

                // подписываемся на ошибку обработки данных (транзакций и маркет)
                _trader.ProcessDataError += error =>
                    this.GuiAsync(() => MessageBox.Show(this, error.ToString(), "Ошибка обработки данных"));

                // подписываемся на ошибку подписки маркет-данных
                _trader.MarketDataSubscriptionFailed += (security, type, error) =>
                    this.GuiAsync(() => MessageBox.Show(this, error.ToString(), LocalizedStrings.Str2956Params.Put(type, security)));

                _trader.Connected += () => this.GuiAsync(() => ExportDde.IsEnabled = true);
                _trader.NewSecurities += securities => this.GuiAsync(() => Security.ItemsSource = _trader.Securities);

                _trader.Connect();

                _candleManager = new CandleManager(_trader);
                _candleManager.Processing += DrawCandle;

                ConnectBtn.IsEnabled = false;
            }
        }

        private void DrawCandle(CandleSeries series, Candle candle)
        {
            var wnd = _chartWindows.TryGetValue(series);

            if (wnd != null)
                wnd.Chart.Draw((ChartCandleElement)wnd.Chart.Areas[0].Elements[0], candle);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            foreach (var pair in _chartWindows)
                pair.Value.DeleteHideable();

            if (_trader != null)
            {
                if (_isDdeStarted)
                    StopDde();

                _trader.Dispose();
            }

            base.OnClosing(e);
        }

        private void StartDde()
        {
            if (_trader.IsDde == true)
                _trader.StartExport(new[] { _trader.SecuritiesTable, _trader.TradesTable });
            else _trader.StartExport();

            _isDdeStarted = true;
        }

        private void StopDde()
        {
            if (_trader.IsDde == true)
                _trader.StopExport(new[] { _trader.SecuritiesTable, _trader.TradesTable });
            else _trader.StopExport();

            _isDdeStarted = false;
        }

        private void ExportDdeClick(object sender, RoutedEventArgs e)
        {
            if (_isDdeStarted)
                StopDde();
            else
                StartDde();
        }

        private CandleTypes SelectedCandleType
        {
            get { return CandleType.GetSelectedValue<CandleTypes>().Value; }
        }

        private Security SelectedSecurity
        {
            get { return (Security)Security.SelectedValue; }
        }

        private void ShowChartClick(object sender, RoutedEventArgs e)
        {
            var type = SelectedCandleType;
            var security = SelectedSecurity;

            CandleSeries series;

            switch (type)
            {
                case CandleTypes.TimeFrame:
                    series = new CandleSeries(typeof(TimeFrameCandle), security, TimeFrame.Value.Value.TimeOfDay);
                    break;
                case CandleTypes.Tick:
                    series = new CandleSeries(typeof(TickCandle), security, VolumeCtrl.Text.To<int>());
                    break;
                case CandleTypes.Volume:
                    series = new CandleSeries(typeof(VolumeCandle), security, VolumeCtrl.Text.To<decimal>());
                    break;
                case CandleTypes.Range:
                    series = new CandleSeries(typeof(RangeCandle), security, PriceRange.Value.Clone().SetSecurity(security));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            _chartWindows.SafeAdd(series, key =>
            {
                var wnd = new ChartWindow
                {
                    Title = "{0} {1} {2}".Put(security.Code, type, series.Arg)
                };

                wnd.MakeHideable();

                var area = new ChartArea();
                wnd.Chart.Areas.Add(area);

                var candlesElem = new ChartCandleElement();
                area.Elements.Add(candlesElem);

                return wnd;
            }).Show();

            _candleManager.Start(series);
        }

        private void SecuritySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var security = SelectedSecurity;

            if (security != null)
            {
                ShowChart.IsEnabled = true;
                PriceRange.Value = 0.5.Percents();
            }
        }

        private void CandleTypesSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var type = SelectedCandleType;
            TimeFrame.IsEnabled = type == CandleTypes.TimeFrame;
            PriceRange.IsEnabled = type == CandleTypes.Range;
            VolumeCtrl.IsEnabled = (!TimeFrame.IsEnabled && !PriceRange.IsEnabled);
        }
    }
}