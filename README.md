# Wallpaper Rotator

Минимальная утилита для Windows 10/11, которая раз в сутки скачивает обои с Wallhaven или NASA Image Library и устанавливает их через WinAPI. У приложения нет телеметрии, рекламы, автообновления, фоновой службы и сторонних NuGet-зависимостей.

## Возможности

- Wallhaven (70% по умолчанию): `general + anime`, SFW, минимум 3840×2160.
- NASA Images (30%): случайный запрос из настраиваемого списка, оригинальный/крупный JPEG.
- Меню в трее, интерактивный выбор темы и отдельного режима SFW/NSFW при ручной смене, возврат к предыдущему фону, пауза и опциональный автозапуск.
- Проверка HTTPS-домена, HTTP/MIME, размера файла, декодируемости, разрешения и ориентации.
- История последних 10 файлов и ссылка на источник каждого текущего изображения.
- Опциональный Wallhaven API-ключ хранится через Windows DPAPI и никогда не записывается в конфигурацию или лог.

## Сборка и проверка

Требуется официальный .NET 8 SDK для Windows x64.

```powershell
dotnet restore WallpaperRotator.sln
dotnet build WallpaperRotator.sln -c Release --no-restore
dotnet run --project tests/WallpaperRotator.Tests -c Release --no-build
dotnet publish src/WallpaperRotator/WallpaperRotator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o outputs/WallpaperRotator-win-x64
```

Список зависимостей можно проверить командой:

```powershell
dotnet list src/WallpaperRotator/WallpaperRotator.csproj package --include-transitive
```

Ожидаемый результат — отсутствие package references. SHA-256 опубликованного файла:

```powershell
Get-FileHash outputs/WallpaperRotator-win-x64/WallpaperRotator.exe -Algorithm SHA256
```

## Использование

Запустите `WallpaperRotator.exe`. Пункт «Выбрать и сменить обои…» или двойной щелчок по значку в трее открывает выбор темы, собственного запроса и режима контента. Выбор запоминается, а пункт «Следующие обои» повторяет его без открытия диалога. Пункт «Настройки…» позволяет выбрать период смены обоев и изменить все параметры конфигурации. Они сохраняются в `%LocalAppData%\WallpaperRotator\config.json`; полный пример находится в `config.example.json`.

Для sketchy/NSFW откройте «Ключ и контент Wallhaven…», введите личный ключ и явно отметьте нужные категории. Без сохранённого ключа приложение всегда запрашивает только SFW. Ключ передаётся Wallhaven в заголовке `X-API-Key`, а не в URL.

Приложение соединяется только с:

- `wallhaven.cc` — официальный API;
- `w.wallhaven.cc` — файлы Wallhaven;
- `images-api.nasa.gov` — официальный API NASA;
- `images-assets.nasa.gov` — файлы NASA.

Сведения об авторе/центре и ссылка на источник сохраняются в `history.json`. Материалы могут иметь отдельные ограничения правообладателей; приложение рассчитано на личное использование.

## Данные и удаление

Все пользовательские данные находятся в `%LocalAppData%\WallpaperRotator`. Чтобы полностью удалить программу:

1. Снимите флажок «Запускать вместе с Windows» и завершите приложение.
2. Удалите EXE и каталог `%LocalAppData%\WallpaperRotator`.

Приложение не создаёт задач Планировщика, служб, драйверов или системных файлов.

## Диагностика и модель безопасности

Ошибки без URL и секретов записываются в `%LocalAppData%\WallpaperRotator\diagnostics.log`. Загрузчик принимает изображения только с двух разрешённых CDN-доменов, ограничивает объём потока и проверяет изображение до изменения рабочего стола. При пяти неудачных попытках текущий фон не меняется.

Перед распространением EXE рекомендуется проверить его Microsoft Defender и сверить SHA-256 со сборкой из исходников.
