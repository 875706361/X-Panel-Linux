const oneMinute = 1000 * 60;  // MilliseConds in a Minute
const oneHour = oneMinute * 60;  // The milliseconds of one hour
const oneDay = oneHour * 24; // The Number of MilliseConds A Day
const oneWeek = oneDay * 7; // The milliseconds per week
const oneMonth = oneDay * 30; // The milliseconds of a month

/**
 * Decrease according to the number of days
 *
 * @param days to reduce the number of days to be reduced
 */
Date.prototype.minusDays = function (days) {
    return this.minusMillis(oneDay * days);
};

/**
 * Increase according to the number of days
 *
 * @param days The number of days to be increased
 */
Date.prototype.plusDays = function (days) {
    return this.plusMillis(oneDay * days);
};

/**
 * A few
 *
 * @param hours to be reduced
 */
Date.prototype.minusHours = function (hours) {
    return this.minusMillis(oneHour * hours);
};

/**
 * Increase hourly
 *
 * @param hours to increase the number of hours
 */
Date.prototype.plusHours = function (hours) {
    return this.plusMillis(oneHour * hours);
};

/**
 * Make reduction in minutes
 *
 * @param minutes to reduce the number of minutes
 */
Date.prototype.minusMinutes = function (minutes) {
    return this.minusMillis(oneMinute * minutes);
};

/**
 * Add in minutes
 *
 * @param minutes to increase the number of minutes
 */
Date.prototype.plusMinutes = function (minutes) {
    return this.plusMillis(oneMinute * minutes);
};

/**
 * Decrease in milliseconds
 *
 * @param millis to reduce the milliseconds
 */
Date.prototype.minusMillis = function(millis) {
    let time = this.getTime() - millis;
    let newDate = new Date();
    newDate.setTime(time);
    return newDate;
};

/**
 * Add in milliseconds to increase
 *
 * @param millis to increase the milliseconds to increase
 */
Date.prototype.plusMillis = function(millis) {
    let time = this.getTime() + millis;
    let newDate = new Date();
    newDate.setTime(time);
    return newDate;
};

/**
 * Setting time is 00: 00: 00.000 on the day
 */
Date.prototype.setMinTime = function () {
    this.setHours(0);
    this.setMinutes(0);
    this.setSeconds(0);
    this.setMilliseconds(0);
    return this;
};

/**
 * Setting time is 23: 59: 59.999 on the same day
 */
Date.prototype.setMaxTime = function () {
    this.setHours(23);
    this.setMinutes(59);
    this.setSeconds(59);
    this.setMilliseconds(999);
    return this;
};

/**
 * Formatting date
 */
Date.prototype.formatDate = function () {
    return this.getFullYear() + "-" + NumberFormatter.addZero(this.getMonth() + 1) + "-" + NumberFormatter.addZero(this.getDate());
};

/**
 * Format time
 */
Date.prototype.formatTime = function () {
    return NumberFormatter.addZero(this.getHours()) + ":" + NumberFormatter.addZero(this.getMinutes()) + ":" + NumberFormatter.addZero(this.getSeconds());
};

/**
 * Formatting date plus time
 *
 * @param split Date and time separation symbols, default is a space
 */
Date.prototype.formatDateTime = function (split = ' ') {
    return this.formatDate() + split + this.formatTime();
};

// 初始化 moment 的中文语言环境（周、月、相对时间等全量中文化）
if (typeof moment !== 'undefined' && typeof moment.defineLocale === 'function') {
    moment.defineLocale('zh-cn', {
        months: '一月_二月_三月_四月_五月_六月_七月_八月_九月_十月_十一月_十二月'.split('_'),
        monthsShort: '1月_2月_3月_4月_5月_6月_7月_8月_9月_10月_11月_12月'.split('_'),
        weekdays: '星期日_星期一_星期二_星期三_星期四_星期五_星期六'.split('_'),
        weekdaysShort: '周日_周一_周二_周三_周四_周五_周六'.split('_'),
        weekdaysMin: '日_一_二_三_四_五_六'.split('_'),
        longDateFormat: {
            LT: 'HH:mm',
            LTS: 'HH:mm:ss',
            L: 'YYYY/MM/DD',
            LL: 'YYYY年M月D日',
            LLL: 'YYYY年M月D日Ah点mm分',
            LLLL: 'YYYY年M月D日ddddAh点mm分',
            l: 'YYYY/M/D',
            ll: 'YYYY年M月D日',
            lll: 'YYYY年M月D日 HH:mm',
            llll: 'YYYY年M月D日dddd HH:mm'
        },
        meridiemParse: /凌晨|早上|上午|中午|下午|晚上/,
        meridiemHour: function (hour, meridiem) {
            if (hour === 12) hour = 0;
            if (meridiem === '凌晨' || meridiem === '早上' || meridiem === '上午') return hour;
            if (meridiem === '下午' || meridiem === '晚上') return hour + 12;
            return hour >= 11 ? hour : hour + 12;
        },
        meridiem: function (hour, minute) {
            const hm = hour * 100 + minute;
            if (hm < 600) return '凌晨';
            if (hm < 900) return '早上';
            if (hm < 1130) return '上午';
            if (hm < 1230) return '中午';
            if (hm < 1800) return '下午';
            return '晚上';
        },
        week: { dow: 1, doy: 4 }
    });
}

class DateUtil {
    // String to date object
    static parseDate(str) {
        return new Date(str.replace(/-/g, '/'));
    }

    static formatMillis(millis) {
        return moment(millis).format('YYYY-M-D HH:mm:ss');
    }

    static firstDayOfMonth() {
        const date = new Date();
        date.setDate(1);
        date.setMinTime();
        return date;
    }

    static convertToJalalian(date) {
        return date && moment.isMoment(date) ? date.format('jYYYY/jMM/jDD HH:mm:ss') : null;
    }

    static addMonthsTo(target, prop, months) {
        let base = target[prop];
        let m;
        if (!base || (typeof base.valueOf === 'function' && base.valueOf() === 0)) {
            m = moment();
        } else {
            m = moment(base);
            // 如果原本的时间已经过期，则从当前时间开始加
            if (m.isBefore(moment())) {
                m = moment();
            }
        }
        target[prop] = m.add(months, 'months');
    }

    static get zhCNPickerLocale() {
        return {
            lang: {
                placeholder: '请选择日期',
                rangePlaceholder: ['开始日期', '结束日期'],
                locale: 'zh_CN',
                today: '今天',
                now: '此刻',
                backToToday: '返回今天',
                ok: '确定',
                clear: '清除',
                month: '月',
                year: '年',
                timeSelect: '选择时间',
                dateSelect: '选择日期',
                monthSelect: '选择月份',
                yearSelect: '选择年份',
                decadeSelect: '选择年代',
                yearFormat: 'YYYY年',
                dateFormat: 'YYYY年M月D日',
                dayFormat: 'D日',
                dateTimeFormat: 'YYYY年M月D日 HH时mm分ss秒',
                monthBeforeYear: false,
                previousMonth: '上个月',
                nextMonth: '下个月',
                previousYear: '上一年',
                nextYear: '下一年',
                previousDecade: '上一年代',
                nextDecade: '下一年代',
                previousCentury: '上一世纪',
                nextCentury: '下一世纪'
            },
            timePickerLocale: {
                placeholder: '请选择时间'
            }
        };
    }
}
