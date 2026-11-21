# Arni (0.1.6)

Лёгкое приложение для Windows 7 SP1 и новее. Чаты с моделями Arni, агент в папке проекта и установка отдельно для 32- и 64-битной Windows. Нужен .NET Framework 4.8.

## Возможности

- Чаты по папке проекта и потоковый ответ модели. Папку можно привязать к открытому чату через «Без проекта» или панель «Файлы».
- Вход через Яндекс или Google в браузере. Сессия хранится только у текущего пользователя Windows.
- Счёт и лимиты аккаунта справа вверху: баланс, пополнение, расходы, тарифы и выход. Звёздочка у суммы отмечает бонус DeepSeek.
- Агент читает и ищет файлы проекта, предлагает правки и выполняет команды после подтверждения.
- Поиск в интернете. В ответ попадают заголовок, адрес и короткий фрагмент, без открытия страницы.
- Несколько MCP-серверов: адрес и необязательный ключ. Подключение проверяется при добавлении.
- Правила проекта из `AGENTS.md` и навыки: встроенные и свои в `.github/skills`.
- Генерация одного изображения PNG в папку проекта.
- Вложения в чат: выбор файлов, перетаскивание, вставка файла и картинки из буфера.
- Просмотр текста, кода и картинок проекта.
- Диктовка с микрофона на русском, английском и казахском. Запись вставляется в черновик и сама не отправляется.
- Язык интерфейса: русский, английский, казахский.
- Проверка обновлений и установка только по кнопке.
- Журнал диагностики без токенов и ключей. Его можно сохранить в ZIP.

## Features

Lightweight client for Windows 7 SP1 and newer. Arni chats, a project agent, and separate 32-bit and 64-bit installers. Requires .NET Framework 4.8.

- Project chats with streaming model responses. Link a folder to an open chat from “No project” or the Files panel.
- Sign-in with Yandex or Google in the browser. The session stays with the current Windows user.
- Account balance and limits at the top right, with top-up, expenses, prices, and sign-out. An asterisk marks a balance that includes a DeepSeek bonus.
- The agent reads and searches project files, proposes edits, and runs commands after confirmation.
- Web search. A result is a title, address, and short snippet. The page itself is not opened.
- Several MCP servers: an address and an optional key. The connection is checked when a server is added.
- Project rules from `AGENTS.md`, plus built-in skills and project skills in `.github/skills`.
- Generation of one PNG image inside the project folder.
- Chat attachments: file picker, drag and drop, and paste of a file or image from the clipboard.
- Preview of project text, code, and images.
- Microphone dictation in Russian, English, and Kazakh. The text is inserted into the draft and is not sent by itself.
- Interface language: Russian, English, and Kazakh.
- Update check, with installation only after the install button.
- A diagnostic log without tokens or keys. It can be saved as a ZIP file.

## Сборка / Build

.NET SDK 8 или новее, пакет .NET Framework 4.8, Windows x64.

```powershell
dotnet test ArniLite.sln -c Release -m:1 -nr:false
dotnet build ArniLite.sln -c Release -m:1 -nr:false
src\ArniLite.App\bin\Release\net48\ArniLite.exe --self-check docs\g0-self-check.txt
```

`ArniLite.Core`, `ArniLite.Api` и `ArniLite.Storage` не обращаются к WPF, реестру, DPAPI и P/Invoke. Запуск процессов, DPAPI и определение системы находятся в `ArniLite.Windows`.
