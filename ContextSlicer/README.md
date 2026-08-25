# ContextSlicer ✂️🤖

A desktop application built with **C# WPF (.NET 8.0/9.0)** designed for efficient source code context management when working with Large Language Models (Google AI Studio, Claude, ChatGPT).

The application allows you to load the structure of any project, select only the specific files required for your current task, and instantly compile their contents into a single optimized context file. This helps focus the AI on specific modules and **significantly saves tokens**.

## 🔥 Core Features

* 📁 **Project & Context Management:** Create independent configurations for different repositories. Within a single project, you can slice multiple task-specific modules (e.g., "Database", "Auth", "UI").
* 🚫 **Smart Garbage Filtering:** Build and cache directories (`bin`, `obj`, `.vs`, `publish`, `.git`, `node_modules`) are automatically excluded from the file tree.
* ⚡ **Asynchronous Network I/O:** Scanning and reading heavy projects (including remote servers and network shares) runs in a background thread. The UI always remains responsive.
* 📦 **Binary Data Filtering:** The app automatically ignores images, audio, videos, archives, logs, and backups (`.bak`, `.tmp`), keeping the context lightweight.
* 🛑 **Progress Overlay with Cancel Button:** A visual operation progress indicator with the ability to instantly abort the network folder reading process.
* 🎨 **Dual Format Export:** Export your context to a classic `.txt` file or a tightly structured `.pdf` (using the monospace `Courier New` font with auto-wrapping for strict AIs).
* 📂 **File Explorer Integration:** Upon successful generation, the app automatically opens the target directory and highlights the created file.
* 🌓 **Dynamic Dark Theme:** A complete dark interface styled after VS Code that saves its state between application launches.
* 🌐 **Automatic Localization:** Automatic interface language switching depending on the operating system language (English / Russian).

---
# ContextSlicer (На русском) ✂️🤖

Десктопное приложение на **C# WPF (.NET 8.0/9.0)** для эффективного управления контекстом исходного кода при работе с большими языковыми моделями (Google AI Studio, Claude, ChatGPT).

Приложение позволяет загрузить структуру любого проекта, выбрать только необходимые для текущей задачи файлы и мгновенно собрать их содержимое в один оптимизированный файл контекста. Это помогает фокусировать ИИ на конкретных модулях и **существенно экономить токены**.

## 🔥 Главные возможности

* 📁 **Управление проектами и контекстами:** Создание независимых конфигураций для разных репозиториев. Внутри одного проекта можно нарезать множество точечных модулей (например, "База данных", "Авторизация", "Интерфейс").
* 🚫 **Умная фильтрация мусора:** Из дерева файлов автоматически исключаются директории сборки и локального кэша (`bin`, `obj`, `.vs`, `publish`, `.git`, `node_modules`).
* ⚡ **Асинхронная работа по сети:** Сканирование и чтение тяжелых проектов (включая удаленные сервера и сетевые папки) происходит в фоновом потоке. Интерфейс приложения всегда остается отзывчивым.
* 📦 **Фильтрация бинарных данных:** Приложение автоматически игнорирует изображения, звуки, архивы, логи и бэкапы (`.bak`, `.tmp`), чтобы не забивать контекст лишним весом.
* 🛑 **Оверлей прогресса с кнопкой отмены:** Наглядный индикатор выполнения операции с возможностью мгновенно прервать процесс чтения сетевой папки.
* 🎨 **Два формата на выбор:** Экспорт контекста в классический `.txt` файл или в жестко структурированный `.pdf` (на моноширинном шрифте `Courier New` с автоматическим переносом длинных строк кода для привередливых ИИ).
* 📂 **Интеграция с проводником:** После генерации приложение автоматически opens целевую папку и подсвечивает созданный файл синим цветом.
* 🌓 **Динамическая тёмная тема:** Полноценный тёмный интерфейс в стиле VS Code, сохраняющий своё состояние при перезапуске приложения.
* 🌐 **Автоматическая локализация:** Автоматическое переключение языка интерфейса в зависимости от настроек вашей операционной системы (English / Русский).
