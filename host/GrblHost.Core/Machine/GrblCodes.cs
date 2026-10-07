namespace GrblHost.Core.Machine;

/// <summary>
/// Texts of the grbl / grblHAL error and alarm codes and of the common $
/// settings, in English and Russian (the controller only sends numbers).
/// </summary>
public static class GrblCodes
{
    private static readonly Dictionary<int, (string En, string Ru)> Errors = new()
    {
        [1] = ("G-code words consist of a letter and a value. Letter was not found.", "Слово G-кода — буква и число. Буква не найдена."),
        [2] = ("Missing the expected G-code word value or numeric value format is not valid.", "Нет значения слова G-кода или неверный формат числа."),
        [3] = ("'$' system command was not recognized or supported.", "Системная команда '$' не распознана или не поддерживается."),
        [4] = ("Negative value received for an expected positive value.", "Отрицательное значение там, где нужно положительное."),
        [5] = ("Homing cycle failure. Homing is not configured via settings.", "Поиск нуля не включён в настройках ($22)."),
        [6] = ("Step pulse time must be greater or equal to 2 microseconds.", "Длительность импульса шага должна быть не меньше 2 мкс."),
        [7] = ("A settings read failed. Auto-restoring affected settings to default values.", "Ошибка чтения настроек. Восстановлены значения по умолчанию."),
        [8] = ("'$' command cannot be used unless controller state is IDLE.", "Команда '$' выполняется только в состоянии Idle."),
        [9] = ("G-code commands are locked out during alarm or jog state.", "G-код заблокирован: тревога или ручное перемещение. Нужен $X или $H."),
        [10] = ("Soft limits cannot be enabled without homing also enabled.", "Программные пределы требуют включённого поиска нуля."),
        [11] = ("Max characters per line exceeded. Received command line was not executed.", "Слишком длинная строка, она не выполнена."),
        [12] = ("'$' setting value cause the step rate to exceed the maximum supported.", "Значение настройки даёт слишком высокую частоту шагов."),
        [13] = ("Safety door detected as opened and door state initiated.", "Открыта защитная дверь."),
        [14] = ("Build info or startup line exceeded line length limit.", "Строка запуска или информации слишком длинная, не сохранена."),
        [15] = ("Jog target exceeds machine travel. Jog command has been ignored.", "Цель перемещения за пределами станка. Команда отброшена."),
        [16] = ("Jog command has no '=' or contains prohibited g-code.", "Команда $J без '=' или с запрещённым G-кодом."),
        [17] = ("Laser mode requires PWM output.", "Режиму лазера нужен выход ШИМ."),
        [18] = ("Reset asserted.", "Активен сброс."),
        [19] = ("Non positive value.", "Значение должно быть больше нуля."),
        [20] = ("Unsupported or invalid g-code command found in block.", "Неподдерживаемая или неверная команда G-кода."),
        [21] = ("More than one g-code command from same modal group found in block.", "Две команды одной модальной группы в одном кадре."),
        [22] = ("Feed rate has not yet been set or is undefined.", "Не задана подача (F)."),
        [23] = ("G-code command in block requires an integer value.", "Команде нужно целое значение."),
        [24] = ("More than one g-code command that requires axis words found in block.", "Две команды, использующие оси, в одном кадре."),
        [25] = ("Repeated g-code word found in block.", "Повтор слова в кадре."),
        [26] = ("No axis words found in block for g-code command or current modal state which requires them.", "В кадре нет координат, а команда их требует."),
        [27] = ("Line number value is invalid.", "Неверный номер строки (N)."),
        [28] = ("G-code command is missing a required value word.", "Не хватает обязательного слова (P, L, ...)."),
        [29] = ("G59.x work coordinate systems are not supported.", "Системы координат G59.x не поддерживаются."),
        [30] = ("G53 only allowed with G0 and G1 motion modes.", "G53 разрешена только с G0 и G1."),
        [31] = ("Axis words found in block when no command or current modal state uses them.", "Координаты в кадре, где их никто не использует."),
        [32] = ("G2 and G3 arcs require at least one in-plane axis word.", "Дуге G2/G3 нужна хотя бы одна координата в плоскости."),
        [33] = ("Motion command target is invalid.", "Неверная цель перемещения."),
        [34] = ("Arc radius value is invalid.", "Неверный радиус дуги."),
        [35] = ("G2 and G3 arcs require at least one in-plane offset word.", "Дуге G2/G3 нужно смещение центра (I/J/K) в плоскости."),
        [36] = ("Unused value words found in block.", "В кадре лишние слова."),
        [37] = ("G43.1 dynamic tool length offset is not assigned to configured tool length axis.", "G43.1: коррекция длины не для оси инструмента."),
        [38] = ("Tool number greater than max supported value or undefined tool selected.", "Номер инструмента больше допустимого."),
        [39] = ("Value out of range.", "Значение вне допустимого диапазона."),
        [40] = ("G-code command not allowed when tool change is pending.", "Команда запрещена до завершения смены инструмента."),
        [41] = ("Spindle not running when motion commanded in CSS or spindle sync mode.", "Шпиндель не вращается."),
        [42] = ("Plane must be ZX for threading.", "Для нарезки резьбы нужна плоскость ZX."),
        [43] = ("Max. feed rate exceeded.", "Превышена максимальная подача."),
        [44] = ("RPM out of range.", "Обороты шпинделя вне диапазона."),
        [45] = ("Only homing is allowed when a limit switch is engaged.", "Сработал концевик: разрешён только поиск нуля."),
        [46] = ("Home machine to continue.", "Нужен поиск нуля ($H)."),
        [47] = ("ATC: current tool is not set. Set current tool with M61.", "Не задан текущий инструмент (M61)."),
        [48] = ("Value word conflict.", "Конфликт слов в кадре."),
        [49] = ("Power on self test failed. A hard reset is required.", "Самотест при включении не прошёл, нужен аппаратный сброс."),
        [50] = ("Emergency stop active.", "Активен аварийный стоп."),
        [51] = ("Motor fault.", "Авария драйвера мотора."),
        [52] = ("Setting value is out of range.", "Значение настройки вне диапазона."),
        [53] = ("Setting is not available, possibly due to limited driver support.", "Настройка недоступна в этой прошивке."),
        [54] = ("Retract position is less than drill depth.", "Позиция отвода ниже глубины сверления."),
        [55] = ("Attempt to home two auto squared axes at the same time.", "Попытка искать ноль двух выравниваемых осей сразу."),
        [56] = ("Coordinate system is locked.", "Система координат заблокирована."),
        [57] = ("Unexpected file demarcation.", "Неожиданный маркер файла (%)."),
        [58] = ("Port is not available.", "Порт недоступен."),
        [60] = ("SD card mount failed.", "Не удалось подключить SD-карту."),
        [61] = ("File read error.", "Ошибка чтения файла."),
        [79] = ("Not allowed while critical event is active.", "Запрещено во время аварийного события."),
    };

    private static readonly Dictionary<int, (string En, string Ru)> Alarms = new()
    {
        [1] = ("Hard limit triggered. Machine position is likely lost, re-homing is recommended.", "Сработал концевик. Позиция, скорее всего, потеряна — нужен поиск нуля."),
        [2] = ("Soft limit: motion target exceeds machine travel. Position retained, unlock with $X.", "Программный предел: цель за пределами станка. Позиция сохранена, $X — разблокировать."),
        [3] = ("Reset while in motion. Machine position is likely lost, re-homing is recommended.", "Сброс во время движения. Позиция, скорее всего, потеряна — нужен поиск нуля."),
        [4] = ("Probe fail: probe not in the expected initial state.", "Зонд: перед началом уже в неожиданном состоянии (касается?)."),
        [5] = ("Probe fail: no contact within the programmed travel.", "Зонд: касания не было на всём пути."),
        [6] = ("Homing fail: the homing cycle was reset.", "Поиск нуля прерван сбросом."),
        [7] = ("Homing fail: safety door opened during homing.", "Поиск нуля: открыта дверь."),
        [8] = ("Homing fail: pull off failed to clear the limit switch.", "Поиск нуля: отвод не освободил концевик. Увеличьте $27 или проверьте проводку."),
        [9] = ("Homing fail: limit switch not found within search distance.", "Поиск нуля: концевик не найден. Проверьте проводку и $130–$132."),
        [10] = ("E-stop asserted. Clear and reset.", "Нажат аварийный стоп. Отпустите и сделайте сброс."),
        [11] = ("Homing required. Execute homing ($H) to continue.", "Требуется поиск нуля ($H)."),
        [12] = ("Limit switch engaged. Clear before continuing.", "Сработал концевик. Освободите его."),
        [13] = ("Probe protection triggered.", "Сработала защита зонда."),
        [14] = ("Spindle at speed timeout.", "Шпиндель не вышел на обороты."),
        [15] = ("Homing fail: second switch of the auto squared axis not found.", "Поиск нуля: не найден второй концевик выравниваемой оси."),
        [16] = ("Power on self test failed.", "Самотест при включении не прошёл."),
        [17] = ("Motor fault.", "Авария драйвера мотора."),
        [18] = ("Homing fail: bad configuration.", "Поиск нуля: неверные настройки."),
        [19] = ("Modbus exception.", "Ошибка Modbus."),
        [20] = ("I/O expander communication failed.", "Ошибка связи с расширителем входов/выходов."),
        [21] = ("Non volatile storage failure.", "Ошибка энергонезависимой памяти настроек."),
        [22] = ("Buffer overflow.", "Переполнение буфера."),
    };

    /// <summary>Name, unit of the common settings ($0 … $132 and some grblHAL ones).</summary>
    private static readonly Dictionary<int, (string En, string Ru, string Unit)> Settings = new()
    {
        [0] = ("Step pulse time", "Длительность импульса шага", "µs"),
        [1] = ("Step idle delay", "Задержка отключения моторов", "ms"),
        [2] = ("Step pulse invert", "Инверсия импульсов шага", "mask"),
        [3] = ("Step direction invert", "Инверсия направления", "mask"),
        [4] = ("Invert stepper enable", "Инверсия разрешения драйверов", "mask"),
        [5] = ("Invert limit pins", "Инверсия концевиков", "mask"),
        [6] = ("Invert probe pin", "Инверсия зонда", "bool"),
        [9] = ("PWM spindle options", "Опции ШИМ шпинделя", "mask"),
        [10] = ("Status report options", "Состав отчёта о состоянии", "mask"),
        [11] = ("Junction deviation", "Отклонение на стыках (junction deviation)", "mm"),
        [12] = ("Arc tolerance", "Точность дуг", "mm"),
        [13] = ("Report in inches", "Отчёты в дюймах", "bool"),
        [14] = ("Invert control pins", "Инверсия входов управления", "mask"),
        [15] = ("Invert coolant pins", "Инверсия выходов охлаждения", "mask"),
        [16] = ("Invert spindle signals", "Инверсия сигналов шпинделя", "mask"),
        [17] = ("Pullup disable control pins", "Отключить подтяжку входов управления", "mask"),
        [18] = ("Pullup disable limit pins", "Отключить подтяжку концевиков", "mask"),
        [19] = ("Pullup disable probe pin", "Отключить подтяжку зонда", "bool"),
        [20] = ("Soft limits enable", "Программные пределы", "bool"),
        [21] = ("Hard limits enable", "Аппаратные пределы (концевики)", "bool"),
        [22] = ("Homing cycle", "Поиск нуля", "mask"),
        [23] = ("Homing direction invert", "Направление поиска нуля", "mask"),
        [24] = ("Homing locate feed rate", "Подача точного поиска нуля", "mm/min"),
        [25] = ("Homing search seek rate", "Скорость поиска нуля", "mm/min"),
        [26] = ("Homing switch debounce delay", "Задержка дребезга концевиков", "ms"),
        [27] = ("Homing switch pull-off distance", "Отвод от концевика", "mm"),
        [28] = ("G73 retract distance", "Отвод G73", "mm"),
        [29] = ("Step pulse delay", "Задержка импульса шага", "µs"),
        [30] = ("Maximum spindle speed", "Максимальные обороты шпинделя", "rpm"),
        [31] = ("Minimum spindle speed", "Минимальные обороты шпинделя", "rpm"),
        [32] = ("Mode of operation (laser)", "Режим работы (1 — лазер)", "enum"),
        [33] = ("Spindle PWM frequency", "Частота ШИМ шпинделя", "Hz"),
        [34] = ("Spindle PWM off value", "ШИМ выключенного шпинделя", "%"),
        [35] = ("Spindle PWM min value", "Минимальный ШИМ шпинделя", "%"),
        [36] = ("Spindle PWM max value", "Максимальный ШИМ шпинделя", "%"),
        [37] = ("Stepper deenergize mask", "Моторы, отключаемые в простое", "mask"),
        [39] = ("Enable legacy RT commands", "Старые команды реального времени (?, !, ~)", "bool"),
        [40] = ("Limit jog commands", "Ограничить ручные перемещения пределами", "bool"),
        [43] = ("Homing passes", "Число проходов поиска нуля", ""),
        [44] = ("Axes homing, first pass", "Оси поиска нуля, первый проход", "mask"),
        [45] = ("Axes homing, second pass", "Оси поиска нуля, второй проход", "mask"),
        [46] = ("Axes homing, third pass", "Оси поиска нуля, третий проход", "mask"),
        [62] = ("Sleep enable", "Спящий режим", "bool"),
        [63] = ("Feed hold actions", "Действия при паузе", "mask"),
        [64] = ("Force init alarm", "Тревога при включении", "bool"),
        [65] = ("Probing feed override", "Коррекция подачи при зондировании", "bool"),
        [100] = ("X-axis travel resolution", "Шагов на мм по X", "step/mm"),
        [101] = ("Y-axis travel resolution", "Шагов на мм по Y", "step/mm"),
        [102] = ("Z-axis travel resolution", "Шагов на мм по Z", "step/mm"),
        [110] = ("X-axis maximum rate", "Максимальная скорость X", "mm/min"),
        [111] = ("Y-axis maximum rate", "Максимальная скорость Y", "mm/min"),
        [112] = ("Z-axis maximum rate", "Максимальная скорость Z", "mm/min"),
        [120] = ("X-axis acceleration", "Ускорение X", "mm/s²"),
        [121] = ("Y-axis acceleration", "Ускорение Y", "mm/s²"),
        [122] = ("Z-axis acceleration", "Ускорение Z", "mm/s²"),
        [130] = ("X-axis maximum travel", "Рабочий ход X", "mm"),
        [131] = ("Y-axis maximum travel", "Рабочий ход Y", "mm"),
        [132] = ("Z-axis maximum travel", "Рабочий ход Z", "mm"),
        [140] = ("X-axis motor current", "Ток мотора X", "mA"),
        [141] = ("Y-axis motor current", "Ток мотора Y", "mA"),
        [142] = ("Z-axis motor current", "Ток мотора Z", "mA"),
        [150] = ("X-axis microsteps", "Микрошаг X", ""),
        [151] = ("Y-axis microsteps", "Микрошаг Y", ""),
        [152] = ("Z-axis microsteps", "Микрошаг Z", ""),
        [220] = ("X-axis jerk", "Рывок X", "mm/s³"),
        [221] = ("Y-axis jerk", "Рывок Y", "mm/s³"),
        [222] = ("Z-axis jerk", "Рывок Z", "mm/s³"),
        [341] = ("Tool change mode", "Режим смены инструмента", "enum"),
        [342] = ("Tool change probing distance", "Путь зонда при смене инструмента", "mm"),
        [343] = ("Tool change locate feed rate", "Подача точного зондирования", "mm/min"),
        [344] = ("Tool change search seek rate", "Скорость поиска зонда", "mm/min"),
        [384] = ("Disable G92 persistence", "Не сохранять G92", "bool"),
        [395] = ("Default spindle", "Шпиндель по умолчанию", "enum"),
    };

    public static string Error(int code, bool russian)
    {
        if (Errors.TryGetValue(code, out var t))
            return russian ? t.Ru : t.En;
        return russian ? $"Ошибка {code}" : $"Error {code}";
    }

    public static string Alarm(int code, bool russian)
    {
        if (Alarms.TryGetValue(code, out var t))
            return russian ? t.Ru : t.En;
        return russian ? $"Тревога {code}" : $"Alarm {code}";
    }

    public static string SettingName(int id, bool russian) =>
        Settings.TryGetValue(id, out var t) ? (russian ? t.Ru : t.En) : "";

    public static string SettingUnit(int id) => Settings.TryGetValue(id, out var t) ? t.Unit : "";

    /// <summary>Alarms after which the position is lost: the machine should be homed again.</summary>
    public static bool AlarmLosesPosition(int code) => code is 1 or 3 or 6 or 8 or 9;
}
