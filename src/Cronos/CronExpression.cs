﻿using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Cronos
{
    public class CronExpression
    {
        private long _second; // 60 bits -> 64 bits in Int64
        private long _minute; // 60 bits -> 64 bits in Int64
        private long _hour; // 24 bits -> 32 bits in Int32
        private long _dayOfMonth; // 31 bits -> 32 bits ini Int32
        private long _month; // 12 bits -> 16 bits in Int16
        private long _dayOfWeek; // 8 bits -> 8 bits in byte

        private int _nthdayOfWeek;
        private bool _nearestWeekday;

        private CronExpression()
        {
        }

        public CronExpressionFlag Flags { get; private set; }

        private static Calendar Calendar => CultureInfo.InvariantCulture.Calendar;

        public static CronExpression Parse(string cronExpression)
        {
            if (string.IsNullOrEmpty(cronExpression)) throw new ArgumentNullException(nameof(cronExpression));

            var expression = new CronExpression();

            unsafe
            {
                fixed (char* value = cronExpression)
                {
                    var fieldsCount = CountFields(value);

                    var pointer = value;

                    pointer = SkipWhiteSpaces(pointer);

                    // Second.

                    if (fieldsCount == Constants.CronWithSecondsFieldsCount)
                    {
                        if (*pointer == '*')
                        {
                            expression.Flags |= CronExpressionFlag.SecondStar;
                        }

                        if ((pointer = GetList(ref expression._second, Constants.FirstSecond, Constants.LastSecond, null, pointer, CronFieldType.Second)) == null)
                        {
                            throw new ArgumentException($"second '{cronExpression}'", nameof(cronExpression));
                        }
                    }
                    else
                    {
                        SetAllBits(out expression._second);
                    }

                    // Minute.

                    if (*pointer == '*')
                    {
                        expression.Flags |= CronExpressionFlag.MinuteStar;
                    }

                    if ((pointer = GetList(ref expression._minute, Constants.FirstMinute, Constants.LastMinute, null, pointer, CronFieldType.Minute)) == null)
                    {
                        throw new ArgumentException($"minute '{cronExpression}'", nameof(cronExpression));
                    }

                    // Hour.

                    if (*pointer == '*')
                    {
                        expression.Flags |= CronExpressionFlag.HourStar;
                    }

                    if ((pointer = GetList(ref expression._hour, Constants.FirstHour, Constants.LastHour, null, pointer, CronFieldType.Hour)) == null)
                    {
                        throw new ArgumentException("hour", nameof(cronExpression));
                    }

                    // Day of month.

                    if (*pointer == '?')
                    {
                        expression.Flags |= CronExpressionFlag.DayOfMonthQuestion;
                    }
                    else if (*pointer == 'L')
                    {
                        expression.Flags |= CronExpressionFlag.DayOfMonthLast;
                    }

                    if ((pointer = GetList(ref expression._dayOfMonth, Constants.FirstDayOfMonth, Constants.LastDayOfMonth, null, pointer, CronFieldType.DayOfMonth)) == null)
                    {
                        throw new ArgumentException("day of month", nameof(cronExpression));
                    }

                    if (*pointer == 'W')
                    {
                        expression._nearestWeekday = true;
                        pointer++;

                        pointer = SkipWhiteSpaces(pointer);
                    }

                    // Month.

                    if ((pointer = GetList(ref expression._month, Constants.FirstMonth, Constants.LastMonth, Constants.MonthNamesArray, pointer, CronFieldType.Month)) == null)
                    {
                        throw new ArgumentException("month", nameof(cronExpression));
                    }

                    // Day of week.

                    if (*pointer == '?' && expression.HasFlag(CronExpressionFlag.DayOfMonthQuestion))
                    {
                        throw new ArgumentException("day of week", nameof(cronExpression));
                    }

                    if ((pointer = GetList(ref expression._dayOfWeek, Constants.FirstDayOfWeek, Constants.LastDayOfWeek, Constants.DayOfWeekNamesArray, pointer, CronFieldType.DayOfWeek)) == null)
                    {
                        throw new ArgumentException("day of week", nameof(cronExpression));
                    }

                    if (*pointer == 'L')
                    {
                        expression.Flags |= CronExpressionFlag.DayOfWeekLast;
                        pointer++;
                    }

                    if (*pointer == '#')
                    {
                        pointer++;
                        pointer = GetNumber(out expression._nthdayOfWeek, Constants.MinNthDayOfWeek, null, pointer);

                        if (expression._nthdayOfWeek < Constants.MinNthDayOfWeek || expression._nthdayOfWeek > Constants.MaxNthDayOfWeek)
                        {
                            throw new ArgumentException("day of week", nameof(cronExpression));
                        }
                    }

                    pointer = SkipWhiteSpaces(pointer);

                    if (*pointer != '\0')
                    {
                        throw new ArgumentException("invalid cron", nameof(cronExpression));
                    }

                    // Make sundays equivilent.
                    if (GetBit(expression._dayOfWeek, 0) || GetBit(expression._dayOfWeek, 7))
                    {
                        SetBit(ref expression._dayOfWeek, 0);
                        SetBit(ref expression._dayOfWeek, 7);
                    }

                    return expression;
                }
            }
        }

        public DateTimeOffset? Next(DateTimeOffset startDateTimeOffset, DateTimeOffset endDateTimeOffset, TimeZoneInfo zone)
        {
            if (zone.Equals(TimeZoneInfo.Utc))
            {
                var found = Next(startDateTimeOffset.DateTime, endDateTimeOffset.DateTime);

                return found != null
                    ? new DateTimeOffset(found.Value, TimeSpan.Zero)
                    : (DateTimeOffset?)null;
            }

            var startLocalDateTime = startDateTimeOffset.DateTime;
            var endLocalDateTime = endDateTimeOffset.DateTime;

            var currentOffset = startDateTimeOffset.Offset;

            var currentAdjusmentRule = GetCurrentAdjusmentRule(zone, startLocalDateTime);

            var dstOffset = currentAdjusmentRule != null
                ? zone.BaseUtcOffset.Add(currentAdjusmentRule.DaylightDelta)
                : zone.BaseUtcOffset;

            if (IsMatch(startLocalDateTime))
            {
                if (zone.IsInvalidTime(startLocalDateTime))
                {
                    var dstTransitionStartDateTimeOffset = GetDstTransitionStartDateTime(currentAdjusmentRule, startLocalDateTime, zone.BaseUtcOffset);

                    return dstTransitionStartDateTimeOffset.ToOffset(dstOffset);
                }
                if (zone.IsAmbiguousTime(startLocalDateTime))
                {
                    // Ambiguous.

                    // Interval jobs should be fired in both offsets.
                    if (HasFlag(CronExpressionFlag.SecondStar | CronExpressionFlag.MinuteStar | CronExpressionFlag.HourStar))
                    {
                        return new DateTimeOffset(startLocalDateTime, currentOffset);
                    }

                    TimeSpan earlyOffset = dstOffset;

                    // Strict jobs should be fired in lowest offset only.
                    if (currentOffset == earlyOffset)
                    {
                        return new DateTimeOffset(startLocalDateTime, currentOffset);
                    }
                }
                else
                {
                    // Strict
                    return new DateTimeOffset(startLocalDateTime, zone.GetUtcOffset(startLocalDateTime));
                }
            }

            if (zone.IsAmbiguousTime(startLocalDateTime.AddTicks(-1)))
            {
                TimeSpan earlyOffset = dstOffset;
                TimeSpan lateOffset = zone.BaseUtcOffset;

                if (earlyOffset == currentOffset)
                {
                    var dstTransitionEndDateTimeOffset = GetDstTransitionEndDateTime(currentAdjusmentRule, startLocalDateTime, earlyOffset);

                    var earlyIntervalLocalEnd = dstTransitionEndDateTimeOffset.AddSeconds(-1).DateTime;

                     // Current period, try to find anything here.
                    var found = Next(startLocalDateTime, earlyIntervalLocalEnd);

                    if (found.HasValue)
                    {
                        return Next(new DateTimeOffset(found.Value, currentOffset), endDateTimeOffset, zone);
                    }

                    var lateIntervalLocalStart = dstTransitionEndDateTimeOffset.ToOffset(lateOffset).DateTime;

                    //Try to find anything starting from late offset.
                    found = Next(lateIntervalLocalStart, endLocalDateTime);

                    if (found.HasValue)
                    {
                        return Next(new DateTimeOffset(found.Value, lateOffset), endDateTimeOffset, zone);
                    }
                }
            }

            // Does not match, find next.
            var nextFound = Next(startLocalDateTime.AddSeconds(1), endLocalDateTime);

            if (nextFound == null) return null;

            return Next(new DateTimeOffset(nextFound.Value, currentOffset), endDateTimeOffset, zone);
        }

        private DateTime? Next(DateTime baseTime, DateTime endTime)
        {
            var baseYear = baseTime.Year;
            var baseMonth = baseTime.Month;
            var baseDay = baseTime.Day;
            var baseHour = baseTime.Hour;
            var baseMinute = baseTime.Minute;
            var baseSecond = baseTime.Second;

            var year = baseYear;
            var month = baseMonth;
            var day = baseDay;
            var hour = baseHour;
            var minute = baseMinute;
            var second = baseSecond;

            var minSecond = FindFirstSet(_second, Constants.FirstSecond, Constants.LastSecond);
            var minMinute = FindFirstSet(_minute, Constants.FirstMinute, Constants.LastMinute);
            var minHour = FindFirstSet(_hour, Constants.FirstHour, Constants.LastHour);
            var minMonth = FindFirstSet(_month, Constants.FirstMonth, Constants.LastMonth);

            //
            // Second.
            //

            var nextSecond = FindFirstSet(_second, second, Constants.LastSecond);

            if (nextSecond != -1)
            {
                second = nextSecond;
            }
            else
            {
                second = minSecond;
                minute++;
            }

            //
            // Minute.
            //

            var nextMinute = FindFirstSet(_minute, minute, Constants.LastMinute);

            if (nextMinute != -1)
            {
                minute = nextMinute;

                if (minute > baseMinute)
                {
                    second = minSecond;
                }
            }
            else
            {
                second = minSecond;
                minute = minMinute;
                hour++;
            }

            //
            // Hour.
            //

            var nextHour = FindFirstSet(_hour, hour, Constants.LastHour);

            if (nextHour != -1)
            {
                hour = nextHour;

                if (hour > baseHour)
                {
                    second = minSecond;
                    minute = minMinute;
                }
            }
            else
            {
                second = minSecond;
                minute = minMinute;
                hour = minHour;
                day++;
            }

            //
            // Day of month.
            //         

            RetryDayMonth:

            day = GetNextDayOfMonth(year, month, day);

            if (day < Constants.FirstDayOfMonth || day > Constants.LastDayOfMonth)
            {
                month++;
                day = GetNextDayOfMonth(year, month, Constants.FirstDayOfMonth);
                second = minSecond;
                minute = minMinute;
                hour = minHour;
            }
            else if (day > baseDay)
            {
                second = minSecond;
                minute = minMinute;
                hour = minHour;
            }

            //
            // Month.
            //

            var nextMonth = FindFirstSet(_month, month, Constants.LastMonth);

            if (nextMonth != -1)
            {
                if (nextMonth > month)
                {
                    second = minSecond;
                    minute = minMinute;
                    hour = minHour;
                    day = GetNextDayOfMonth(year, nextMonth, Constants.FirstDayOfMonth);
                }

                month = nextMonth;
            }
            else
            {
                second = minSecond;
                minute = minMinute;
                hour = minHour;
                month = minMonth;
                year++;
                day = GetNextDayOfMonth(year, month, Constants.FirstDayOfMonth);
            }

            if (day < Constants.FirstDayOfMonth || day > Constants.LastDayOfMonth)
            {
                if (new DateTime(year, month, Constants.FirstDayOfMonth, hour, minute, second) > endTime) return null;

                day = -1;
                goto RetryDayMonth;
            }

            var dayOfWeek = Calendar.GetDayOfWeek(new DateTime(year, month, day));
            var lastDayOfMonth = Calendar.GetDaysInMonth(year, month);

            // W character.

            if (_nearestWeekday)
            {
                var nearestWeekDay = GetNearestWeekDay(day, dayOfWeek, lastDayOfMonth);

                if (nearestWeekDay > day)
                {
                    // Day was shifted from Saturday or Sunday to Monday.
                    hour = minHour;
                    minute = minMinute;
                    second = minSecond;
                    dayOfWeek = DayOfWeek.Monday;
                }
                else if (nearestWeekDay < day)
                {
                    // Day was shifted from Saturday or Sunday to Friday.
                    dayOfWeek = DayOfWeek.Friday;

                    if (month == baseMonth && year == baseYear)
                    {
                        if (nearestWeekDay < baseDay || nearestWeekDay == baseDay && nextHour == -1)
                        {
                            day = -1;
                        }
                        else if (nearestWeekDay == baseDay)
                        {
                            // Recover hour, minute and second matched for baseDay.
                            hour = nextHour;
                            minute = nextHour > baseHour ? minMinute : nextMinute;
                            second = nextMinute > baseMinute ? minSecond : nextSecond;
                        }
                    }
                }

                if (new DateTime(year, month, nearestWeekDay, hour, minute, second) > endTime)
                    return null;

                if (((_dayOfWeek >> (int)dayOfWeek) & 1) == 0) day = -1;

                if (HasFlag(CronExpressionFlag.DayOfWeekLast) && !IsLastDayOfWeek(nearestWeekDay, lastDayOfMonth))
                {
                    day = -1;
                }

                if (_nthdayOfWeek != 0 && !IsNthDayOfWeek(nearestWeekDay, _nthdayOfWeek))
                {
                    day = -1;
                }

                if (day == -1) goto RetryDayMonth;

                day = nearestWeekDay;
            }

            if (new DateTime(year, month, day, hour, minute, second) > endTime)
            {
                return null;
            }

            //
            // Day of week.
            //

            if (((_dayOfWeek >> (int)dayOfWeek) & 1) == 0)
            {
                second = minSecond;
                minute = minMinute;
                hour = minHour;
                day++;

                goto RetryDayMonth;
            }

            // L character in day of week.

            if (HasFlag(CronExpressionFlag.DayOfWeekLast) && !IsLastDayOfWeek(day, lastDayOfMonth))
            {
                second = minSecond;
                minute = minMinute;
                hour = minHour;
                day++;

                goto RetryDayMonth;
            }

            // # character.

            if (_nthdayOfWeek != 0 && !IsNthDayOfWeek(day, _nthdayOfWeek))
            {
                second = minSecond;
                minute = minMinute;
                hour = minHour;
                day++;

                goto RetryDayMonth;
            }

            return new DateTime(year, month, day, hour, minute, second);
        }

        private DateTimeOffset GetDstTransitionEndDateTime(TimeZoneInfo.AdjustmentRule rule, DateTime ambiguousDateTime, TimeSpan dstOffset)
        {
            var transitionTime = rule.DaylightTransitionStart.TimeOfDay;
            
            var transitionDateTime = new DateTimeOffset(
                    ambiguousDateTime.Year,
                    ambiguousDateTime.Month,
                    ambiguousDateTime.Day,
                    transitionTime.Hour,
                    transitionTime.Minute,
                    transitionTime.Second,
                    transitionTime.Millisecond,
                    dstOffset);

            if (transitionDateTime.TimeOfDay < ambiguousDateTime.TimeOfDay)
            {
                transitionDateTime = transitionDateTime.AddDays(1);
            }

            return transitionDateTime;
        }

        private DateTimeOffset GetDstTransitionStartDateTime(TimeZoneInfo.AdjustmentRule rule, DateTime invalidDateTime, TimeSpan baseOffset)
        {
            var transitionTime = rule.DaylightTransitionStart.TimeOfDay;

            var transitionDateTime = new DateTimeOffset(
                invalidDateTime.Year,
                invalidDateTime.Month,
                invalidDateTime.Day,
                transitionTime.Hour,
                transitionTime.Minute,
                transitionTime.Second,
                transitionTime.Millisecond,
                baseOffset);

            if (invalidDateTime.TimeOfDay < transitionTime.TimeOfDay)
            {
                transitionDateTime = transitionDateTime.AddDays(-1);
            }

            return transitionDateTime;
        }

        private TimeZoneInfo.AdjustmentRule GetCurrentAdjusmentRule(TimeZoneInfo zone, DateTime now)
        {
            var rules = zone.GetAdjustmentRules();
            for (var i = 0; i < rules.Length; i++)
            {
                if (rules[i].DateStart < now && rules[i].DateEnd > now) return rules[i];
            }
            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsNthDayOfWeek(int day, int n)
        {
            return day - Constants.DaysPerWeekCount * n < Constants.FirstDayOfMonth &&
                   day - Constants.DaysPerWeekCount * (n - 1) >= Constants.FirstDayOfMonth;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsLastDayOfWeek(int day, int lastDayOfMonth)
        {
            return day + Constants.DaysPerWeekCount > lastDayOfMonth;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FindFirstSet(long value, int startBit, int endBit)
        {
            return DeBruijin.FindFirstSet(value, startBit, endBit);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetNearestWeekDay(int day, DayOfWeek dayOfWeek, int lastDayOfMonth)
        {
            if (dayOfWeek == DayOfWeek.Sunday)
            {
                if (day == lastDayOfMonth)
                {
                    return day - 2;
                }
                return day + 1;
            }
            if (dayOfWeek == DayOfWeek.Saturday)
            {
                if (day == Constants.FirstDayOfMonth)
                {
                    return day + 2;
                }
                return day - 1;
            }
            return day;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetNextDayOfMonth(int year, int month, int startDay)
        {
            if (month < Constants.FirstMonth || month > Constants.LastMonth) return -1;

            if (startDay == -1) return -1;

            var daysInMonth = Calendar.GetDaysInMonth(year, month);

            var dayOfMonthField = HasFlag(CronExpressionFlag.DayOfMonthLast)
                   ? _dayOfMonth >> (Constants.LastDayOfMonth - daysInMonth)
                   : _dayOfMonth;

            var nextDay = FindFirstSet(dayOfMonthField, startDay, daysInMonth);

            if (nextDay == -1) return -1;

            return nextDay;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool HasFlag(CronExpressionFlag flag)
        {
            return (Flags & flag) != 0;
        }

        private bool IsMatch(int second, int minute, int hour, int dayOfMonth, int month, int dayOfWeek, int year)
        {
            var daysInMonth = Calendar.GetDaysInMonth(year, month);

            var dayOfMonthField = HasFlag(CronExpressionFlag.DayOfMonthLast)
                    ? _dayOfMonth >> (Constants.LastDayOfMonth - daysInMonth)
                    : _dayOfMonth;

            if (HasFlag(CronExpressionFlag.DayOfMonthLast) && !_nearestWeekday)
            {
                if (!GetBit(dayOfMonthField, dayOfMonth)) return false;
            }
            else if (HasFlag(CronExpressionFlag.DayOfWeekLast))
            {
                if (!IsLastDayOfWeek(dayOfMonth, daysInMonth)) return false;
            }
            else if (_nthdayOfWeek != 0)
            {
                if(!IsNthDayOfWeek(dayOfMonth, _nthdayOfWeek)) return false;
            }
            else if (_nearestWeekday)
            {
                var isDayMatched = GetBit(dayOfMonthField, dayOfMonth) && dayOfWeek > 0 && dayOfWeek < 6 ||
                                   GetBit(dayOfMonthField, dayOfMonth - 1) && dayOfWeek == 1 ||
                                   GetBit(dayOfMonthField, dayOfMonth + 1) && dayOfWeek == 5 ||
                                   GetBit(dayOfMonthField, 1) && dayOfWeek == 1 && (dayOfMonth == 2 || dayOfMonth == 3) ||
                                   GetBit(dayOfMonthField, dayOfMonth + 2) && dayOfMonth == daysInMonth - 2 && dayOfWeek == 5;

                if (!isDayMatched) return false;
            }

            // Make 0-based values out of these so we can use them as indicies
            // minute -= Constants.FirstMinute;
            //  hour -= Constants.FirstHour;
            // dayOfMonth -= Constants.FirstDayOfMonth;
            //  month -= Constants.FirstMonth;
            // dayOfWeek -= Constants.FirstDayOfWeek;

            // The dom/dow situation is:  
            //     "* * 1,15 * Sun" will run on the first and fifteenth *only* on Sundays; 
            //     "* * * * Sun" will run *only* on Sundays; 
            //     "* * 1,15 * *" will run *only* the 1st and 15th.
            // this is why we keep DayOfMonthStar and DayOfWeekStar.
            return GetBit(_second, second) &&
                   GetBit(_minute, minute) &&
                   GetBit(_hour, hour) &&
                   GetBit(_month, month) &&
                   GetBit(_dayOfWeek, dayOfWeek) &&
                   (_nearestWeekday || GetBit(dayOfMonthField, dayOfMonth));
        }

        private bool IsMatch(DateTime dateTime)
        {
            return IsMatch(
                dateTime.Second,
                dateTime.Minute,
                dateTime.Hour,
                dateTime.Day,
                dateTime.Month,
                (int)dateTime.DayOfWeek,
                dateTime.Year);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetAllBits(out long bits)
        {
            bits = ~0L;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe char* SkipWhiteSpaces(char* pointer)
        {
            while (*pointer == '\t' || *pointer == ' ')
            {
                pointer++;
            }

            return pointer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe char* GetList(
          ref long bits, /* one bit per flag, default=FALSE */
          int low, int high, /* bounds, impl. offset for bitstr */
          int[] names, /* NULL or *[] of names for these elements */
          char* pointer,
          CronFieldType cronFieldType)
        {
            var singleValue = true;
            while (true)
            {
                if ((pointer = GetRange(ref bits, low, high, names, pointer, cronFieldType)) == null)
                {
                    return null;
                }

                if (*pointer == ',')
                {
                    singleValue = false;
                    pointer++;
                }
                else
                {
                    break;
                }
            }

            if (*pointer == 'W' && !singleValue)
            {
                return null;
            }

            // exiting.  skip to some blanks, then skip over the blanks.
            /*while (*pointer != '\t' && *pointer != ' ' && *pointer != '\n' && *pointer != '\0')
            {
                pointer++;
            }*/

            pointer = SkipWhiteSpaces(pointer);

            return pointer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe char* GetRange(
            ref long bits, 
            int low, 
            int high,
            int[] names,
            char* pointer,
            CronFieldType cronFieldType)
        {
            int num1, num2, num3;
            
            if (*pointer == '*')
            {
                // '*' means "first-last" but can still be modified by /step.
                num1 = low;
                num2 = high;

                pointer++;

                if (*pointer != '/')
                {
                    SetAllBits(out bits);
                    return pointer;
                }
            }
            else if(*pointer == '?')
            {
                if (cronFieldType != CronFieldType.DayOfMonth && cronFieldType != CronFieldType.DayOfWeek)
                {
                    return null;
                }

                pointer++;

                if (*pointer == '/') return null;

                SetAllBits(out bits);
                return pointer;
            }
            else if(*pointer == 'L')
            {
                if (cronFieldType != CronFieldType.DayOfMonth) return null;

                pointer++;

                SetBit(ref bits, Constants.LastDayOfMonth);

                if (*pointer == '-')
                {
                    // Eat the dash.
                    pointer++;

                    int lastMonthOffset;

                    // Get the number following the dash.
                    if ((pointer = GetNumber(out lastMonthOffset, 0, null, pointer)) == null)
                    {
                        return null;
                    }

                    if (lastMonthOffset < 0 || lastMonthOffset >= high) return null;

                    bits = bits >> lastMonthOffset;
                }
                return pointer;
            }
            else
            {
                if ((pointer = GetNumber(out num1, low, names, pointer)) == null)
                {
                    return null;
                }

                // Explicitly check for sane values. Certain combinations of ranges and
                // steps which should return EOF don't get picked up by the code below,
                // eg:
                //     5-64/30 * * * *
                //
                // Code adapted from set_elements() where this error was probably intended
                // to be catched.
                if (num1 < low || num1 > high)
                {
                    return null;
                }

                if (*pointer == '-')
                {
                    // Eat the dash.
                    pointer++;

                    // Get the number following the dash.
                    if ((pointer = GetNumber(out num2, low, names, pointer)) == null)
                    {
                        return null;
                    }

                    // Explicitly check for sane values. Certain combinations of ranges and
                    // steps which should return EOF don't get picked up by the code below,
                    // eg:
                    //     5-64/30 * * * *
                    //
                    if (num2 < low || num2 > high) return null;

                    if (*pointer == 'W') return null;
                }
                else if (*pointer == '/')
                {
                    num2 = high;
                }
                else
                {
                    SetBit(ref bits, num1);

                    return pointer;
                }
            }

            // check for step size
            if (*pointer == '/')
            {
                // eat the slash
                pointer++;

                // Get the step size -- note: we don't pass the
                // names here, because the number is not an
                // element id, it's a step size.  'low' is
                // sent as a 0 since there is no offset either.
                if ((pointer = GetNumber(out num3, 0, null, pointer)) == null || num3 <= 0 || num3 > high)
                {
                    return null;
                }
                if (*pointer == 'W')
                {
                    return null;
                }
            }
            else
            {
                // No step.  Default==1.
                num3 = 1;
            }

            // If upper bound less than bottom one, e.g. range 55-10 specified
            // we'll set bits from 0 to 15 then we shift it right by 5 bits.
            int shift = 0;
            if (num2 < num1)
            {
                // Skip one of sundays.
                if (cronFieldType == CronFieldType.DayOfWeek) high--;

                shift = high - num1 + 1;
                num2 = num2 + shift;
                num1 = low;
            }

            // Range. set all elements from num1 to num2, stepping
            // by num3.
            for (var i = num1; i <= num2; i += num3)
            {
                SetBit(ref bits, i);
            }

            // If we have range like 55-10 or 11-1, so num2 > num1 we have to shift bits right.
            bits = shift == 0 
                ? bits 
                : bits >> shift | bits << (high - low - shift + 1);

            return pointer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe char* GetNumber(
            out int num, /* where does the result go? */
            int low, /* offset applied to result if symbolic enum used */
            int[] names, /* symbolic names, if any, for enums */
            char* pointer)
        {
            num = 0;

            if (IsDigit(*pointer))
            {
                num = GetNumeric(*pointer++);

                if (!IsDigit(*pointer)) return pointer;

                num = (num << 3) + (num << 1) + GetNumeric(*pointer++);

                if (!IsDigit(*pointer)) return pointer;

                return null;

                /*do
                {
                    num = (num << 3) + (num << 1) + GetNumeric(*pointer++);
                } while (IsDigit(*pointer));

                return pointer;*/
            }

            if (names == null) return null;

            if (!IsLetter(*pointer)) return null;
            var buffer = ToUpper(*pointer++);

            if (!IsLetter(*pointer)) return null;
            buffer |= ToUpper(*pointer++) << 8;

            if (!IsLetter(*pointer)) return null;
            buffer |= ToUpper(*pointer++) << 16;

            if (IsLetter(*pointer)) return null;

            var length = names.Length;

            for (var i = 0; i < length; i++)
            {
                if (buffer == names[i])
                {
                    num = i + low;
                    return pointer;
                }
            }

            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool GetBit(long value, int index)
        {
            return (value & (1L << index)) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetBit(ref long value, int index)
        {
            value |= 1L << index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsDigit(int code)
        {
            return code >= 48 && code <= 57;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsLetter(int code)
        {
            return (code >= 65 && code <= 90) || (code >= 97 && code <= 122);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetNumeric(int code)
        {
            return code - 48;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ToUpper(int code)
        {
            if (code >= 97 && code <= 122)
            {
                return code - 32;
            }

            return code;
        }

        private static unsafe int CountFields(char* pointer)
        {
            int length = 0;
            while (*(pointer = SkipWhiteSpaces(pointer)) != '\0')
            {
                while (*pointer != '\t' && *pointer != ' ' && *pointer != '\0')
                {
                    pointer++;
                }
                length++;
            }
            return length;
        }
    }
}