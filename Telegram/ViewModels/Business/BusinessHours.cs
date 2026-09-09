//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Split out of ViewModels/Business/BusinessHoursViewModel.cs (same partial namespace, no code
// changed). Reason: Controls/Cells/ProfileHoursCell -- the opening-hours row of a business
// account's profile header -- needs BusinessHoursRange and BusinessDay and nothing else from that
// file, while BusinessHoursViewModel itself pulls in BusinessFeatureViewModelBase and the whole of
// Views/Business/Popups, none of which is in the Linux subset. These two are plain data types over
// System, Formatter, Strings and LocaleService. Same treatment as ForumTopicCell.Icon.cs and
// OrbitGenerator.cs.

using System;
using System.Collections.Generic;
using System.Linq;
using Telegram.Common;
using Telegram.Converters;
using Telegram.Services;

namespace Telegram.ViewModels.Business
{
    public partial class BusinessHoursRange
    {
        public BusinessHoursRange(TimeSpan start, TimeSpan end)
        {
            Start = start;
            End = end;
        }

        public BusinessHoursRange(int start, int end)
        {
            Start = TimeSpan.FromHours(start);
            End = TimeSpan.FromHours(end);
        }

        public static BusinessHoursRange FromMinutes(int start, int end)
        {
            return new BusinessHoursRange(TimeSpan.FromMinutes(start), TimeSpan.FromMinutes(end));
        }

        public TimeSpan Start { get; set; }

        public TimeSpan End { get; set; }

        public override string ToString()
        {
            var start = Formatter.Time(DateTime.Today.Add(Start));
            var end = Formatter.Time(DateTime.Today.Add(End));

            if (End.TotalHours > 24)
            {
                end = string.Format(Strings.BusinessHoursNextDay, end);
            }

            return string.Format("{0} - {1}", start, end);
        }
    }

    public partial class BusinessDay
    {
        public BusinessDay(DayOfWeek dayOfWeek)
        {
            DayOfWeek = dayOfWeek;
        }

        public DayOfWeek DayOfWeek { get; }

        public int IndexOfWeek
        {
            get
            {
                return DayOfWeek switch
                {
                    DayOfWeek.Monday => 0,
                    DayOfWeek.Tuesday => 1,
                    DayOfWeek.Wednesday => 2,
                    DayOfWeek.Thursday => 3,
                    DayOfWeek.Friday => 4,
                    DayOfWeek.Saturday => 5,
                    DayOfWeek.Sunday => 6,
                    _ => 0
                };
            }
        }

        public int StartMinute => IndexOfWeek * 60 * 24;
        public int EndMinute => StartMinute + 60 * 24;

        public List<BusinessHoursRange> Ranges { get; } = new();

        public string Name => LocaleService.Current.CurrentCulture.DateTimeFormat.GetDayName(DayOfWeek);

        public string Description
        {
            get
            {
                if (Ranges.Count == 0)
                {
                    return Strings.BusinessHoursDayClosed;
                }
                else if (Ranges.Count == 1 && Ranges[0].Start == TimeSpan.Zero && Ranges[0].End == TimeSpan.FromHours(24))
                {
                    return Strings.BusinessHoursDayFullOpened;
                }

                return string.Join(", ", Ranges);
            }
        }

        public string Description2
        {
            get
            {
                if (Ranges.Count == 0)
                {
                    return Strings.BusinessHoursProfileClose;
                }
                else if (Ranges.Count == 1 && Ranges[0].Start == TimeSpan.Zero && Ranges[0].End == TimeSpan.FromHours(24))
                {
                    return Strings.BusinessHoursProfileOpen;
                }

                return string.Join("\n", Ranges);
            }
        }

        public string DescriptionAt(TimeSpan offset)
        {
            if (Ranges.Count == 0)
            {
                return Strings.BusinessHoursProfileClose;
            }
            else if (Ranges.Count == 1 && Ranges[0].Start == TimeSpan.Zero && Ranges[0].End == TimeSpan.FromHours(24))
            {
                return Strings.BusinessHoursProfileOpen;
            }

            return string.Join("\n", Ranges.Where(x => x.Start > offset || (x.Start < offset && x.End > offset)));
        }

        public bool IsOpen => Ranges.Count > 0;

        public bool IsOpen24 => Ranges.Count == 1 && Ranges[0].Start == TimeSpan.Zero && Ranges[0].End == TimeSpan.FromHours(24);

        public bool IsOpenAt(TimeSpan time)
        {
            return Ranges.Any(x => time.IsBetween(x.Start, x.End));
        }

        public static bool GetRelativeRange2(int start, int end, int rangeStart, int rangeEnd, int tolerance, out int newStart, out int newEnd, out bool consumed)
        {
            // Included, Included
            if (start > rangeStart && end <= rangeEnd)
            {
                newStart = start;
                newEnd = end;
                consumed = true;
            }
            // Before, Included
            else if (start <= rangeStart && end > rangeStart && end < rangeEnd)
            {
                newStart = rangeStart;
                newEnd = end;
                consumed = false;
            }
            // Included, After
            else if (start > rangeStart && start < rangeEnd && end > rangeEnd)
            {
                if (end <= rangeEnd + tolerance)
                {
                    newStart = start;
                    newEnd = end;
                    consumed = true;
                }
                else
                {
                    newStart = start;
                    newEnd = rangeEnd;
                    consumed = false;
                }
            }
            // Before, After
            else if (start <= rangeStart && end >= rangeEnd)
            {
                newStart = rangeStart;
                newEnd = rangeEnd;
                consumed = false;
            }
            else
            {
                newStart = -1;
                newEnd = end;
                consumed = false;
                return false;
            }

            return true;
        }
    }
}
