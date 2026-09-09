//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using LinqToVisualTree;
using System;
using System.Collections.Generic;
using System.Linq;
using Telegram.Controls;
using Telegram.Converters;
using Telegram.Native;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td.Api;
using Windows.System.UserProfile;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FontWeights = Microsoft.UI.Text.FontWeights;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Views.Popups
{
    public sealed partial class CalendarPopup : ContentPopup
    {
        private readonly IClientService _clientService;
        private readonly long _chatId;
        private readonly MessageTopic _messageTopic;

        private readonly DispatcherTimer _throttleTimer;

        private bool _loadingMessages;
        private bool _hasMoreMessages = true;
        private long _fromMessageId;
        private DateTime _fromMessageDate = DateTime.MaxValue;
        private DateTime _minimumDate = DateTime.MaxValue;

        private bool _programmaticChange;

        public CalendarPopup(IClientService clientService, long chatId, MessageTopic topic, DateTimeOffset? date = null)
        {
            InitializeComponent();

            _clientService = clientService;
            _chatId = chatId;
            _messageTopic = topic;
            _fromMessageId = 0;

            _throttleTimer = new DispatcherTimer();
            _throttleTimer.Interval = TimeSpan.FromMilliseconds(50);
            _throttleTimer.Tick += Throttle_Tick;

#if LINUX
            // Both of these THROW in Uno, and they threw in the constructor, which is why the date
            // pill of the chat history and the calendar button of a profile were dead: measured on
            // 2026-08-27 with the app on screen -- clicking "18 de agosto" produced
            // `System.NotImplementedException: The member IReadOnlyList<string>
            // GlobalizationPreferences.Calendars is not implemented` on the dispatcher and no
            // popup at all. Uno.dll marks Windows.System.UserProfile.GlobalizationPreferences
            // `[NotImplemented(... "__SKIA__" ...)]` on Calendars, Clocks, Currencies,
            // HomeGeographicRegion and WeekStartsOn alike: every one of them is a `throw`, not a
            // default.
            //
            // CalendarIdentifier is left alone on purpose: its default is already the Gregorian
            // calendar, which is what Calendars.FirstOrDefault() returns on every desktop this
            // port targets. The first day of the week does have a real answer here, and it is the
            // one the rest of the desktop uses -- .NET reads it from the same CLDR data GNOME
            // does, so a Spanish session gets Monday and a US one Sunday, exactly like Windows.
            View.FirstDayOfWeek = (Windows.Globalization.DayOfWeek)(int)System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            View.Language = NativeUtils.GetCurrentCulture();
#else
            View.CalendarIdentifier = GlobalizationPreferences.Calendars.FirstOrDefault();
            View.FirstDayOfWeek = GlobalizationPreferences.WeekStartsOn;
            View.Language = NativeUtils.GetCurrentCulture();
#endif

            if (date.HasValue)
            {
                View.SelectedDates.Add(date.Value);
                View.SetDisplayDate(date.Value);
            }

            View.SelectedDatesChanged += OnSelectedDatesChanged;

            var multiple = true;

            PrimaryButtonText = multiple ? Strings.SelectDays : Strings.OK;
            SecondaryButtonText = Strings.Close;

            InitializeCalendar();
        }

        private void Throttle_Tick(object sender, object e)
        {
            _throttleTimer.Stop();

            if (_fromMessageDate > _minimumDate && _hasMoreMessages)
            {
                InitializeCalendar();
            }
        }

        private async void InitializeCalendar()
        {
            if (_loadingMessages)
            {
                return;
            }

            _loadingMessages = true;

            // u-091: _loadingMessages is a latch, and that is why this one needs more than a log.
            // The guard above returns early while it is set, and the only place that clears it is
            // the assignment near the end of the body - so a throw anywhere in between leaves it
            // set for the life of the popup and EVERY later call returns immediately: the calendar
            // stops filling for good, with no empty-state and (uncaught) one anonymous dispatcher
            // line. So the catch releases the latch as well as logging it.
            //
            // Released in catch and not in a finally, deliberately: the success path clears the
            // latch BEFORE the recursive tail call below, and that call is itself async void, so it
            // returns to us at its first await while its own pass is still in flight. A finally
            // would then run and write false over the true that the in-flight pass had just set,
            // re-opening the guard it depends on. Only the failing path needs the release, and only
            // the failing path gets it.
            try
            {
                var response = await _clientService.SendAsync(new GetChatMessageCalendar(_chatId, _messageTopic, new SearchMessagesFilterPhotoAndVideo(), _fromMessageId));
                if (response is MessageCalendar calendar)
                {
                    foreach (var day in calendar.Days)
                    {
                        var date = Formatter.ToLocalTime(day.Message.Date);

                        _dateToMessage[date.Date] = day.Message;

                        _fromMessageId = day.Message.Id;
                        _fromMessageDate = date.Date;

                        if (_dateToSelector.TryGetValue(date.Date, out CalendarViewDayItem item))
                        {
                            UpdateDayItem(item, day.Message);
                        }
                    }

                    _hasMoreMessages = calendar.Days.Count > 0;
                }
                else
                {
                    _hasMoreMessages = false;
                }

                _loadingMessages = false;

                if (_fromMessageDate > _minimumDate && _hasMoreMessages)
                {
                    InitializeCalendar();
                }
            }
            catch (Exception ex)
            {
                _loadingMessages = false;
                Logger.Error(ex);
            }
        }

        private void UpdateDayItem(CalendarViewDayItem item, Message message)
        {
            var children = item.Elements().ToList();

            var grid = children[0] as Grid;
            var border = grid.Children[0] as ImageView;
            var text = grid.Children[1] as TextBlock;
            var original = children[^1] as TextBlock;

            //grid.Background = new SolidColorBrush(Colors.Black);
            //text.Foreground = new SolidColorBrush(Colors.White);
            text.Text = original.Text;
            //text.FontWeight = FontWeights.SemiBold;
            grid.Visibility = Visibility.Visible;
            original.Visibility = Visibility.Collapsed;

            if (message.Content is MessagePhoto photo)
            {
                border.SetSource(_clientService, photo.Photo.GetSmall().Photo, photo.Photo.Minithumbnail);
            }
            else if (message.Content is MessageVideo video)
            {
                if (video.Cover != null)
                {
                    border.SetSource(_clientService, video.Cover.GetSmall()?.Photo, video.Cover.Minithumbnail);
                }
                else
                {
                    border.SetSource(_clientService, video.Video.Thumbnail?.File, video.Video.Minithumbnail);
                }
            }
        }

        private void UpdateDayItem(CalendarViewDayItem item)
        {
            var children = item.Elements().ToList();

            var grid = children[0] as Grid;
            var original = children[^1] as TextBlock;

            grid.Visibility = Visibility.Collapsed;
            original.Visibility = Visibility.Visible;
        }

        private void OnSelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
        {
            if (sender.SelectionMode == CalendarViewSelectionMode.Multiple && !_programmaticChange)
            {
                if (args.AddedDates.Count > 1)
                {
                    return;
                }
                else if (args.AddedDates.Count == 1)
                {
                    _programmaticChange = true;

                    if (sender.SelectedDates.Count == 2)
                    {
                        var min = sender.SelectedDates[0] > sender.SelectedDates[1] ? sender.SelectedDates[1] : sender.SelectedDates[0];
                        var max = sender.SelectedDates[0] > sender.SelectedDates[1] ? sender.SelectedDates[0] : sender.SelectedDates[1];

                        var diff = max - min;

                        for (int i = 1; i < diff.TotalDays; i++)
                        {
                            sender.SelectedDates.Add(min.AddDays(i));
                        }
                    }
                    else
                    {
                        sender.SelectedDates.Clear();
                        sender.SelectedDates.Add(args.AddedDates[0]);
                    }

                    _programmaticChange = false;
                }
                else if (args.RemovedDates.Count == 1)
                {
                    _programmaticChange = true;

                    sender.SelectedDates.Clear();
                    sender.SelectedDates.Add(args.RemovedDates[0]);

                    _programmaticChange = false;
                }
            }
            else if (sender.SelectionMode == CalendarViewSelectionMode.Single)
            {
                if (sender.SelectedDates.Count == 1)
                {
                    _programmaticChange = true;
                    Hide(ContentDialogResult.Primary);
                }
            }
        }

        public DateTimeOffset MinDate
        {
            get => View.MinDate;
            set => View.MinDate = value;
        }

        public DateTimeOffset MaxDate
        {
            get => View.MaxDate;
            set => View.MaxDate = value;
        }

        public IList<DateTimeOffset> SelectedDates => View.SelectedDates;

        public bool ClearHistory { get; private set; }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (View.SelectionMode == CalendarViewSelectionMode.Single && !_programmaticChange)
            {
                SetValue(PrimaryButtonStyleProperty, BootStrapper.Current.Resources["DangerButtonStyle"] as Style);

                PrimaryButtonText = Strings.ClearHistory;
                SecondaryButtonText = Strings.Cancel;

                DefaultButton = ContentDialogButton.None;

                View.SelectedDates.Clear();
                View.SelectionMode = CalendarViewSelectionMode.Multiple;
                args.Cancel = true;
            }
            else if (View.SelectionMode == CalendarViewSelectionMode.Multiple && SelectedDates.Count > 1)
            {
                ClearHistory = true;
            }
        }

        private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (View.SelectionMode == CalendarViewSelectionMode.Multiple)
            {
                ClearValue(PrimaryButtonStyleProperty);

                PrimaryButtonText = Strings.SelectDays;
                SecondaryButtonText = Strings.Close;

                DefaultButton = ContentDialogButton.Primary;

                View.SelectionMode = CalendarViewSelectionMode.Single;
                View.SelectedDates.Clear();
                args.Cancel = true;
            }
        }

        private readonly Dictionary<DateTime, CalendarViewDayItem> _dateToSelector = new();
        private readonly Dictionary<DateTime, Message> _dateToMessage = new();

        private void OnCalendarViewDayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
        {
            if (args.InRecycleQueue || args.Item.Date.Date.Year < 2013)
            {
                _dateToSelector.Remove(args.Item.Date.Date);
                return;
            }

            _dateToSelector[args.Item.Date.Date] = args.Item;

            if (_dateToMessage.TryGetValue(args.Item.Date.Date, out Message message))
            {
                UpdateDayItem(args.Item, message);
            }
            else
            {
                UpdateDayItem(args.Item);
            }

            if (args.Item.Date.Date < _minimumDate && _hasMoreMessages)
            {
                _minimumDate = args.Item.Date.Date;

                _throttleTimer.Stop();
                _throttleTimer.Start();
            }
        }
    }
}
