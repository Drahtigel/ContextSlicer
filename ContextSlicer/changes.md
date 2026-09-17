# Список изменений / Changelog

## [Русский язык]

### 1. Валидация и защита данных
* **Проверка на дубликаты:** При создании нового проекта или добавлении модуля система проверяет имя на уникальность (без учета регистра). Если имя занято, выводится предупреждение.
* **Общий метод валидации (`IsValidName`):** Реализована единая функция проверки имен. Она запрещает спецсимволы Windows (`\`, `/`, `:`, `*`, `?`, `"`, `<`, `>`, `|`) и пустые строки.
* **Интеграция с редактированием «на лету»:** Метод валидации интегрирован в команды изменения текущих имён. При вводе некорректного имени старое значение восстанавливается.
* **Визуальная индикация ошибок:** Если имя не проходит валидацию, рамка текстового поля подсвечивается ярко-красным цветом. При начале исправления подсветка автоматически исчезает.

### 2. Логика интерфейса и автоматизация
* **Автопереключение модулей:** После успешного добавления нового модуля комбобокс «Выбор модуля» автоматически переключается на него, а не сбрасывается на нулевой индекс.
* **Очистка полей:** Текстовые поля ввода для создания проектов и модулей теперь автоматически очищаются сразу после успешного добавления элемента.
* **Автосохранение:** Внедрено тихое автосохранение структуры в файл `projects.json` сразу после создания или изменения любого проекта/модуля.

### 3. Исправление и оптимизация UI (WPF)
* **Адаптивная верстка:** Исправлена проблема перекрытия кнопок в блоке модулей при сужении окна. Фиксированная `StackPanel` заменена на адаптивную сетку `Grid` с динамическими колонками. Кнопка «Добавить модуль» перенесена непосредственно к полю ввода его имени.
* **Разделение правил кода (Prompt):** Поле общих правил разделено на два независимых текстовых поля: «Общие правила проекта» и «Правила текущего модуля». В разметку XAML добавлен горизонтальный разделитель (`GridSplitter`) для ручной регулировки их высоты.
* **Управление структурой каталогов:** Добавлен чекбокс «Включать структуру каталогов» в блок управления проектом. Его состояние сохраняется в глобальных настройках приложения и в JSON-конфигурации проекта.

### 4. Генерация контекста для ИИ
* **Изоляция блоков:** В `ContextBuilderService` обновлена логика склейки текста. Правила проекта и модуля теперь оборачиваются в XML-теги (`<project_rules>` и `<module_rules>`), что улучшает понимание контекста нейросетями.
* **Скрытие структуры:** Если чекбокс структуры каталогов снят, блок структуры полностью исключается из итогового TXT/PDF файла без добавления лишних комментариев.
* **Локализация:** Добавлены все новые строковые ресурсы в файлы русской (`Strings.ru.xaml`) и английской (`Strings.en.xaml`) локализации.

### 5. Подсчет объема контекста (Символы и Токены)
* **Динамический счетчик:** В нижнюю панель приложения встроен асинхронный калькулятор объема данных, отображающий общее количество символов и примерный вес в токенах.
* **Адаптивная математика:** Расчет учитывает специфику кодирования текста ИИ: вес исходного кода оценивается из расчета ~4 символа на токен, а кириллические правила (Prompt) — из расчета ~2 символа на токен.
* **Связь с настройками UI:** Счетчик мгновенно и асинхронно реагирует на любые изменения: ввод символов в полях правил, выбор/снятие чекбоксов в дереве файлов, а также на переключение флага «Включать структуру каталогов» (вес строк структуры динамически добавляется или вычитается из общего объема).

---

## [English]

### 1. Validation & Data Protection
* **Duplicate Prevention:** When creating a new project or adding a module, the system checks for name uniqueness (case-insensitive) and shows a warning if it already exists.
* **Unified Validation Method (`IsValidName`):** A single function was implemented to check names. It restricts illegal Windows filesystem characters (`\`, `/`, `:`, `*`, `?`, `"`, `<`, `>`, `|`) and empty strings.
* **On-the-fly Editing Integration:** The validation method is integrated into the commands modifying existing names. If an invalid name is entered, the previous valid name is restored.
* **Visual Error Indication:** If a name fails validation, the text box border turns bright red. The red highlight disappears automatically as soon as the user starts typing to fix the error.

### 2. Interface Logic & Automation
* **Auto-switching Modules:** After successfully adding a new module, the "Select Module" ComboBox automatically switches to it instead of defaulting to the first index.
* **Input Clearing:** Text boxes for project and module creation are now automatically cleared right after a new item is successfully added.
* **Autosave:** Silent autosaving to the `projects.json` file is executed immediately after creating or modifying any project or module.

### 3. UI Fixes & Optimization (WPF)
* **Responsive Layout:** Fixed the issue where buttons overlapped inside the module management block when narrowing the window. The fixed `StackPanel` was replaced with a responsive `Grid` utilizing dynamic columns. The "Add Module" button was moved right next to the module name input text box.
* **Split Prompt Rules:** The prompt rules section was split into two independent text areas: "Project General Rules" and "Current Module Rules". A horizontal `GridSplitter` was added to XAML to allow manual height adjustments between them.
* **Directory Structure Toggle:** Added an "Include directory structure" checkbox to the project management block. Its state is fully preserved in the global app settings and the project's JSON configuration.

### 4. AI Context Generation
* **Block Isolation:** Updated text compilation logic in `ContextBuilderService`. Project and module rules are now wrapped in XML tags (`<project_rules>` and `<module_rules>`) for better LLM context understanding.
* **Structure Hiding:** If the directory structure checkbox is unchecked, the entire folder tree section is completely removed from the output TXT/PDF file without adding unnecessary meta-comments.
* **Localization:** Added all new string assets to the Russian (`Strings.ru.xaml`) and English (`Strings.en.xaml`) localization resource dictionaries.

### 5. Context Volume Calculation (Characters & Tokens)
* **Dynamic Counter:** An asynchronous data volume calculator has been integrated into the bottom panel, displaying the total number of characters and the estimated token weight.
* **Adaptive Mathematics:** The calculation accounts for LLM tokenization specifics: source code is estimated at ~4 characters per token, while Cyrillic prompt rules are evaluated at ~2 characters per token.
* **UI State Binding:** The counter instantly and asynchronously updates on any user action: typing in rules textboxes, checking/unchecking files in the tree, or toggling the "Include directory structure" checkbox (the tree structure weight is dynamically added or omitted).